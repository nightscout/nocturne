using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Reports;

/// <summary>What the report builder may compose: the limits of a spec and every dataset it may name.</summary>
/// <param name="MaxPanels">The most panels one report holds.</param>
/// <param name="MaxTitleLength">The longest report or panel title.</param>
/// <param name="MaxRangeHours">The longest range any dataset covers, in hours.</param>
/// <param name="MaxNameLength">The longest name a saved report may have.</param>
public sealed record CustomReportCatalogue(
    int MaxPanels,
    int MaxTitleLength,
    int MaxRangeHours,
    int MaxNameLength,
    IReadOnlyList<CustomReportDataset> Datasets);

/// <summary>A dataset a panel may read.</summary>
/// <param name="Name">The name a spec gives it.</param>
/// <param name="Label">Its name in words.</param>
/// <param name="Description">One line that says what it holds.</param>
/// <param name="RequiredScope">The read scope without which it is not read at all.</param>
/// <param name="Permitted">Whether the caller holds <paramref name="RequiredScope"/>.</param>
/// <param name="Parameters">Its parameters as JSON Schema (<see cref="ReportParametersSchema"/>).</param>
/// <param name="Fields">Every field of its rows, in the tenant's glucose unit.</param>
/// <param name="Primitives">The primitives that may draw it, with their bindings.</param>
/// <param name="DefaultRangeHours">The range a panel gets when neither it nor the report gives one.</param>
/// <param name="MaxPanelsPerReport">The most panels of this dataset one report holds.</param>
public sealed record CustomReportDataset(
    string Name,
    string Label,
    string Description,
    string RequiredScope,
    bool Permitted,
    JsonElement Parameters,
    IReadOnlyList<CustomReportField> Fields,
    IReadOnlyList<CustomReportDatasetPrimitive> Primitives,
    double DefaultRangeHours,
    int MaxPanelsPerReport);

/// <summary>A field of a dataset's rows.</summary>
/// <param name="Kind">time, number, category, percent, offset (minutes from an event) or time_of_day.</param>
/// <param name="Scope">The read scope it needs beyond the dataset's, or null.</param>
/// <param name="Permitted">Whether the caller holds the dataset's scope and <paramref name="Scope"/>.</param>
public sealed record CustomReportField(
    string Name,
    string Label,
    string Kind,
    string? Unit,
    string Description,
    string? Scope,
    bool Permitted);

/// <summary>A primitive that may draw a dataset.</summary>
/// <param name="Name">The name a spec gives it.</param>
/// <param name="Label">Its name in words.</param>
/// <param name="MaxRangeHours">The longest range the dataset covers drawn this way, in hours.</param>
/// <param name="RangeLimit">That limit as a sentence.</param>
/// <param name="DefaultBindings">The bindings a new panel starts with, as the validator normalises them.</param>
/// <param name="Bindings">The bindings this primitive takes and the fields each may name.</param>
public sealed record CustomReportDatasetPrimitive(
    string Name,
    string Label,
    double MaxRangeHours,
    string RangeLimit,
    CustomReportBindings DefaultBindings,
    IReadOnlyList<CustomReportBindingSlot> Bindings);

/// <summary>A panel's bindings, as a spec writes them.</summary>
public sealed record CustomReportBindings(
    string? X,
    IReadOnlyList<string>? Y,
    IReadOnlyList<string>? Band,
    string? Series,
    IReadOnlyList<string>? Columns,
    bool? Stacked,
    string? Orientation);

/// <summary>One binding a primitive takes.</summary>
/// <param name="Key">The bindings key: x, y, band, series, columns, stacked or orientation.</param>
/// <param name="Input">field (one field), fields (an ordered list), boolean or choice.</param>
/// <param name="Fields">The fields it may name, for field and fields.</param>
/// <param name="Counts">
/// The numbers of fields it takes, for fields: [2] for a shaded range's edges, [2, 4] for bands;
/// empty for any number from one.
/// </param>
/// <param name="Required">Whether a panel must give it; a missing optional one is left out.</param>
/// <param name="Choices">The values it takes, for choice.</param>
public sealed record CustomReportBindingSlot(
    string Key,
    string Label,
    string Input,
    IReadOnlyList<string> Fields,
    IReadOnlyList<int> Counts,
    bool Required,
    IReadOnlyList<CustomReportChoice> Choices);

/// <summary>A value a choice takes, with its words.</summary>
public sealed record CustomReportChoice(string Value, string Label);

/// <summary>A spec the builder offers as a start.</summary>
/// <param name="Spec">The normalised spec.</param>
public sealed record CustomReportTemplate(string Id, string Name, string Description, JsonElement Spec);

/// <summary>The report builder's catalogue and templates, for the request's caller.</summary>
public interface ICustomReportCatalogue
{
    Task<CustomReportCatalogue> CatalogueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomReportTemplate>> TemplatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the catalogue from the registered datasets. It reads no data: what it says of a dataset
/// is the same for every caller but whether the caller may read it, and the glucose unit.
/// </summary>
internal sealed class ReportCatalogue(
    IReportDatasetRegistry registry,
    ReportSpecValidator validator,
    IHttpContextAccessor httpContextAccessor,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    IOptions<MvcJsonOptions> jsonOptions) : ICustomReportCatalogue
{
    public async Task<CustomReportCatalogue> CatalogueAsync(CancellationToken cancellationToken) =>
        Build(
            httpContextAccessor.HttpContext?.GetGrantedScopes() ?? new HashSet<string>(),
            await GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken));

    public async Task<IReadOnlyList<CustomReportTemplate>> TemplatesAsync(CancellationToken cancellationToken)
    {
        var useMmol = await GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
        return [.. ReportTemplates.Normalised(validator, useMmol).Select(t => new CustomReportTemplate(
            t.Template.Id,
            t.Template.Name,
            t.Template.Description,
            JsonSerializer.SerializeToElement(t.Spec, jsonOptions.Value.JsonSerializerOptions)))];
    }

    internal CustomReportCatalogue Build(IReadOnlySet<string> grantedScopes, bool useMmol) => new(
        ReportSpec.MaxPanels,
        ReportSpec.MaxTitleLength,
        ReportSpec.MaxRangeHours,
        ReportSpecStore.MaxNameLength,
        [.. registry.All.Select(dataset => Describe(dataset, grantedScopes, useMmol))]);

    private CustomReportDataset Describe(IReportDataset dataset, IReadOnlySet<string> granted, bool useMmol)
    {
        var permitted = Scope.Satisfies(granted, dataset.RequiredScope);
        return new CustomReportDataset(
            dataset.Name,
            dataset.Label,
            dataset.Description,
            dataset.RequiredScope,
            permitted,
            ReportParametersSchema.For(dataset.ParametersType),
            [.. dataset.Fields.Select(f => f.ResolveUnit(useMmol)).Select(f => new CustomReportField(
                f.Name, f.Label, Wire(f.Kind), f.Unit, f.Description, f.Scope,
                permitted && (f.Scope is null || Scope.Satisfies(granted, f.Scope))))],
            [.. dataset.AllowedPrimitives.Select(p => Primitive(dataset, p, useMmol))],
            dataset.DefaultRange.TotalHours,
            dataset.MaxPanelsPerReport);
    }

    private CustomReportDatasetPrimitive Primitive(IReportDataset dataset, ReportPrimitive primitive, bool useMmol)
    {
        var max = dataset.MaxRange(primitive);
        return new CustomReportDatasetPrimitive(
            primitive.WireName(),
            PrimitiveLabel(primitive),
            max.TotalHours,
            $"This panel covers at most {ReportSpecValidator.Describe(max)}.",
            DefaultBindings(dataset, primitive, useMmol),
            Slots(dataset, primitive));
    }

    /// <summary>
    /// The bindings a new panel starts with: the dataset's own defaults, else the first number field
    /// as the one line or bar, normalised by the validator so they are the ones a run would use.
    /// </summary>
    internal CustomReportBindings DefaultBindings(IReportDataset dataset, ReportPrimitive primitive, bool useMmol)
    {
        var first = dataset.FieldsFor(primitive).FirstOrDefault(f => !f.Companion && IsNumber(f));
        object? bindings = dataset.DefaultBindings(primitive) is null
            && primitive is ReportPrimitive.TimeSeries or ReportPrimitive.Bar
            && first is not null
                ? new { y = new[] { first.Name } }
                : null;
        var hours = Math.Min(dataset.DefaultRange.TotalHours, dataset.MaxRange(primitive).TotalHours);
        var spec = new Dictionary<string, object?>
        {
            ["version"] = ReportSpec.CurrentVersion,
            ["panels"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["dataset"] = new { name = dataset.Name },
                    ["primitive"] = primitive.WireName(),
                    ["range"] = new { hours = (int)Math.Max(1, hours) },
                    ["bindings"] = bindings,
                },
            },
        };
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol);
        var normalised = validation.Spec?.Panels[0].Bindings
            ?? throw new InvalidOperationException(
                $"{dataset.Name} as {primitive.WireName()} has no default bindings: " + string.Join("; ", validation.Errors));
        return new CustomReportBindings(
            normalised.X,
            normalised.Y,
            normalised.Band,
            normalised.Series,
            normalised.Columns,
            normalised.Stacked,
            normalised.Orientation switch
            {
                ReportBarOrientation.Horizontal => "horizontal",
                ReportBarOrientation.Vertical => "vertical",
                _ => null,
            });
    }

    /// <summary>The bindings <paramref name="primitive"/> takes over <paramref name="dataset"/>, as the validator checks them.</summary>
    internal static IReadOnlyList<CustomReportBindingSlot> Slots(IReportDataset dataset, ReportPrimitive primitive)
    {
        var fields = dataset.FieldsFor(primitive).Where(f => !f.Companion).ToList();
        IReadOnlyList<string> Of(Func<ReportField, bool> keep) => [.. fields.Where(keep).Select(f => f.Name)];
        var axis = Of(f => f.Kind is ReportFieldKind.Time or ReportFieldKind.TimeOfDay or ReportFieldKind.Offset);
        var aligned = Of(f => f.Kind is ReportFieldKind.TimeOfDay or ReportFieldKind.Offset);
        var numbers = Of(IsNumber);
        var categories = Of(f => f.Kind == ReportFieldKind.Category);

        static CustomReportBindingSlot Field(string key, string label, IReadOnlyList<string> names, bool required) =>
            new(key, label, "field", names, [], required, []);
        static CustomReportBindingSlot Fields(string key, string label, IReadOnlyList<string> names, IReadOnlyList<int> counts, bool required) =>
            new(key, label, "fields", names, counts, required, []);

        var bandsLike = primitive == ReportPrimitive.RangeBands
            || (primitive == ReportPrimitive.GlucoseChart && dataset.DefaultBindings(ReportPrimitive.GlucoseChart) is not null);
        if (bandsLike)
            return
            [
                Field("x", "Horizontal axis", aligned, false),
                Fields("y", "Middle line", numbers, [1], true),
                Fields("band", "Bands: low and high edges", numbers, [2, 4], true),
                Field("series", "One set of bands for each", categories, false),
            ];

        return primitive switch
        {
            ReportPrimitive.TimeSeries =>
            [
                Field("x", "Horizontal axis", axis, false),
                Fields("y", "Lines", numbers, [], true),
                Fields("band", "Shaded range: low and high edge", numbers, [2], false),
            ],
            ReportPrimitive.Bar =>
            [
                Field("x", "Categories", categories, false),
                Fields("y", "Bars", numbers, [], true),
                new("stacked", "Stack the bars", "boolean", [], [], false, []),
                new("orientation", "Bar direction", "choice", [], [], false,
                    [new CustomReportChoice("vertical", "Vertical"), new CustomReportChoice("horizontal", "Horizontal")]),
            ],
            ReportPrimitive.Table or ReportPrimitive.StatTiles or ReportPrimitive.Comparison =>
            [
                Fields("columns", "Columns", Of(_ => true), [], false),
            ],
            _ => [],
        };
    }

    internal static string PrimitiveLabel(ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.TimeSeries => "Lines over time",
        ReportPrimitive.StatTiles => "Figures",
        ReportPrimitive.Table => "Table",
        ReportPrimitive.GlucoseChart => "Glucose chart",
        ReportPrimitive.RangeBands => "Percentile bands",
        ReportPrimitive.Bar => "Bars",
        ReportPrimitive.Comparison => "Two periods side by side",
        _ => throw new ArgumentOutOfRangeException(nameof(primitive), primitive, null),
    };

    private static bool IsNumber(ReportField field) => field.Kind is ReportFieldKind.Number or ReportFieldKind.Percent;

    private static string Wire(ReportFieldKind kind) => JsonSerializer.Serialize(kind).Trim('"');
}
