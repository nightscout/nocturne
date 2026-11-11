using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Reports.Derived;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Reports;

/// <summary>What the report builder may compose: the limits of a spec, every dataset, mark and derived op, and the workspace vocabulary.</summary>
/// <param name="MaxPanels">The most panels one report holds.</param>
/// <param name="MaxTracks">The most tracks one chart holds.</param>
/// <param name="MaxLayers">The most layers one chart holds.</param>
/// <param name="MaxUnitsPerTrack">The most units one track's layers on an axis may be in: a left and a right axis.</param>
/// <param name="MaxReads">The most dataset reads one report makes, a derived layer's sources counted; one query read twice counts once.</param>
/// <param name="MaxTitleLength">The longest report, panel, track or layer title.</param>
/// <param name="MaxRangeHours">The longest range any dataset covers, in hours.</param>
/// <param name="MaxNameLength">The longest name a saved report may have.</param>
/// <param name="MaxGridMinutes">The coarsest grid a chart may give, in minutes.</param>
public sealed record CustomReportCatalogue(
    [property: Required] int MaxPanels,
    [property: Required] int MaxTracks,
    [property: Required] int MaxLayers,
    [property: Required] int MaxUnitsPerTrack,
    [property: Required] int MaxReads,
    [property: Required] int MaxTitleLength,
    [property: Required] int MaxRangeHours,
    [property: Required] int MaxNameLength,
    [property: Required] int MaxGridMinutes,
    [property: Required] IReadOnlyList<CustomReportPanelKind> PanelKinds,
    [property: Required] IReadOnlyList<CustomReportMark> Marks,
    [property: Required] IReadOnlyList<CustomReportOp> Ops,
    [property: Required] IReadOnlyList<CustomReportDataset> Datasets,
    [property: Required] CustomReportWorkspace Workspace);

/// <summary>A kind of panel, with its words.</summary>
public sealed record CustomReportPanelKind([property: Required] string Name, [property: Required] string Label);

/// <summary>A mark a layer is drawn as.</summary>
/// <param name="HasAxis">Whether it draws on a vertical axis; markers and spans do not.</param>
/// <param name="XKinds">The horizontal axes it may lie along.</param>
/// <param name="Slots">Its bindings and the field kinds each accepts.</param>
public sealed record CustomReportMark(
    [property: Required] string Name,
    [property: Required] string Label,
    [property: Required] bool HasAxis,
    [property: Required] IReadOnlyList<string> XKinds,
    [property: Required] IReadOnlyList<CustomReportMarkSlot> Slots);

/// <summary>One binding of a mark.</summary>
/// <param name="Accepts">The field kinds it takes (time, number, ...), or "marker" for a field with a treatment marker.</param>
/// <param name="Counts">For a list: the numbers of fields it takes; empty for any number from one.</param>
public sealed record CustomReportMarkSlot(
    [property: Required] string Key,
    [property: Required] string Label,
    [property: Required] string Input,
    [property: Required] IReadOnlyList<string> Accepts,
    [property: Required] IReadOnlyList<int> Counts,
    [property: Required] bool Required);

/// <summary>A series the API derives from one or two sources.</summary>
/// <param name="Sources">1 or 2.</param>
/// <param name="Parameters">Its settings beside its sources, as JSON Schema.</param>
/// <param name="XRule">The axis the series lies along, in words.</param>
/// <param name="UnitRule">The series' unit, in words.</param>
/// <param name="SourceXKinds">The axes a source may lie along.</param>
/// <param name="OutputXKinds">The axis a layer of it lies along for a source along each of <paramref name="SourceXKinds"/>, in the same order.</param>
/// <param name="SourceFieldKinds">The kinds of field a source may use; the field must also have an aggregate.</param>
/// <param name="Marks">The marks that may draw it, the default first, each with its bindings.</param>
public sealed record CustomReportOp(
    [property: Required] string Name,
    [property: Required] string Label,
    [property: Required] string Description,
    [property: Required] int Sources,
    [property: Required] JsonElement Parameters,
    [property: Required] string XRule,
    [property: Required] string UnitRule,
    [property: Required] IReadOnlyList<string> SourceXKinds,
    [property: Required] IReadOnlyList<string> OutputXKinds,
    [property: Required] IReadOnlyList<string> SourceFieldKinds,
    [property: Required] IReadOnlyList<CustomReportOpMark> Marks);

/// <summary>A mark that may draw a derived series, with the bindings it starts with.</summary>
public sealed record CustomReportOpMark([property: Required] string Name, [property: Required] CustomReportLayerBindings DefaultBindings);

/// <summary>A dataset a panel or a layer may read.</summary>
/// <param name="Name">The name a spec gives it.</param>
/// <param name="Label">Its name in words.</param>
/// <param name="Description">One line that says what it holds.</param>
/// <param name="RequiredScope">The read scope without which it is not read at all.</param>
/// <param name="Permitted">Whether the caller holds <paramref name="RequiredScope"/>.</param>
/// <param name="Parameters">Its parameters as JSON Schema (<see cref="ReportParametersSchema"/>).</param>
/// <param name="Fields">Every field of its rows, in the tenant's glucose unit.</param>
/// <param name="XKinds">The horizontal axes its layers may lie along.</param>
/// <param name="PanelKinds">The kinds of panel other than a chart that may show it, each with the fields it may show.</param>
/// <param name="Marks">The marks a chart layer of it may be drawn as, the default first, with their bindings.</param>
/// <param name="DefaultRangeHours">The range a panel gets when neither it nor the report gives one.</param>
/// <param name="MaxRangeHours">The longest range one read of it covers, in hours.</param>
/// <param name="RangeLimit">That limit as a sentence.</param>
/// <param name="MaxReadsPerReport">The most reads of it one report makes.</param>
/// <param name="SeriesField">
/// The field naming the group a row is in, for a dataset whose rows come in groups (the event groups of
/// event_response); a derived source of it names the group it reads. Null for a dataset in one group.
/// </param>
/// <param name="SeriesParameter">The parameter that lists the groups, one item each, for a dataset with <paramref name="SeriesField"/>.</param>
/// <param name="DefaultSeriesCount">The groups a query of it that lists none comes in (every meal: 1); null for a dataset in one group.</param>
public sealed record CustomReportDataset(
    [property: Required] string Name,
    [property: Required] string Label,
    [property: Required] string Description,
    [property: Required] string RequiredScope,
    [property: Required] bool Permitted,
    [property: Required] JsonElement Parameters,
    [property: Required] IReadOnlyList<CustomReportField> Fields,
    [property: Required] IReadOnlyList<string> XKinds,
    [property: Required] IReadOnlyList<CustomReportDatasetPanelKind> PanelKinds,
    [property: Required] IReadOnlyList<CustomReportDatasetMark> Marks,
    [property: Required] double DefaultRangeHours,
    [property: Required] double MaxRangeHours,
    [property: Required] string RangeLimit,
    [property: Required] int MaxReadsPerReport,
    string? SeriesField,
    string? SeriesParameter,
    int? DefaultSeriesCount);

/// <summary>A field of a dataset's rows.</summary>
/// <param name="Kind">time, number, category, percent, offset (minutes from an event), time_of_day or day.</param>
/// <param name="Scope">The read scope it needs beyond the dataset's, or null.</param>
/// <param name="Permitted">Whether the caller holds the dataset's scope and <paramref name="Scope"/>.</param>
/// <param name="Aggregate">How it folds onto a chart's grid: sum, last, mean or max; null when it cannot be folded.</param>
/// <param name="Marker">The treatment glyph a markers layer draws for it, or null.</param>
/// <param name="Setting">A value of the profile's settings, which no derived layer takes as a source.</param>
public sealed record CustomReportField(
    [property: Required] string Name,
    [property: Required] string Label,
    [property: Required] string Kind,
    string? Unit,
    [property: Required] string Description,
    string? Scope,
    [property: Required] bool Permitted,
    string? Aggregate,
    string? Marker,
    [property: Required] bool Setting);

/// <summary>A kind of panel that may show a dataset, and the fields it may show.</summary>
public sealed record CustomReportDatasetPanelKind([property: Required] string Name, [property: Required] IReadOnlyList<string> Columns);

/// <summary>A mark that may draw a dataset.</summary>
/// <param name="DefaultBindings">The bindings a new layer starts with, as the validator normalises them.</param>
/// <param name="Bindings">The bindings this mark takes and the fields of the dataset each may name.</param>
public sealed record CustomReportDatasetMark(
    [property: Required] string Name,
    [property: Required] CustomReportLayerBindings DefaultBindings,
    [property: Required] IReadOnlyList<CustomReportBindingSlot> Bindings);

/// <summary>A layer's bindings, as a spec writes them.</summary>
public sealed record CustomReportLayerBindings(
    string? X,
    IReadOnlyList<string>? Y,
    IReadOnlyList<string>? Edges,
    string? Series,
    string? Fit,
    bool? Stacked,
    string? Orientation,
    string? Start,
    string? End,
    string? Category);

/// <summary>One binding a mark takes over a dataset.</summary>
/// <param name="Key">The bindings key: x, y, edges, series, fit, stacked, orientation, start, end or category.</param>
/// <param name="Input">field (one field), fields (an ordered list), boolean or choice.</param>
/// <param name="Fields">The fields it may name, for field and fields.</param>
/// <param name="Counts">The numbers of fields it takes, for fields: [2, 4] for a band's edges; empty for any number from one.</param>
/// <param name="Required">Whether a layer must give it; a missing optional one is left out.</param>
/// <param name="Choices">The values it takes, for choice.</param>
public sealed record CustomReportBindingSlot(
    [property: Required] string Key,
    [property: Required] string Label,
    [property: Required] string Input,
    [property: Required] IReadOnlyList<string> Fields,
    [property: Required] IReadOnlyList<int> Counts,
    [property: Required] bool Required,
    [property: Required] IReadOnlyList<CustomReportChoice> Choices);

/// <summary>A value a choice takes, with its words.</summary>
public sealed record CustomReportChoice([property: Required] string Value, [property: Required] string Label);

/// <summary>A spec the builder offers as a start.</summary>
/// <param name="Spec">The normalised spec.</param>
public sealed record CustomReportTemplate([property: Required] string Id, [property: Required] string Name, [property: Required] string Description, [property: Required] JsonElement Spec);

/// <summary>The report builder's catalogue and templates, for the request's caller.</summary>
public interface ICustomReportCatalogue
{
    Task<CustomReportCatalogue> CatalogueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomReportTemplate>> TemplatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the catalogue from the registered datasets, marks and ops. It reads no data: what it says
/// is the same for every caller but whether the caller may read each dataset and field, and the glucose unit.
/// </summary>
internal sealed class ReportCatalogue(
    IReportDatasetRegistry registry,
    ReportSpecValidator validator,
    IHttpContextAccessor httpContextAccessor,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    IOptions<MvcJsonOptions> jsonOptions) : ICustomReportCatalogue
{
    private static readonly IReadOnlyList<CustomReportChoice> Orientations =
        [new CustomReportChoice("vertical", "Vertical"), new CustomReportChoice("horizontal", "Horizontal")];

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
        ReportSpec.MaxTracks,
        ReportSpec.MaxLayers,
        ReportSpec.MaxUnitsPerTrack,
        ReportSpec.MaxReads,
        ReportSpec.MaxTitleLength,
        ReportSpec.MaxRangeHours,
        ReportSpecStore.MaxNameLength,
        ReportGrid.MaxMinutes,
        [.. Enum.GetValues<ReportPanelKind>().Select(k => new CustomReportPanelKind(ReportWire.Name(k), PanelKindLabel(k)))],
        [.. ReportMarks.All.Select(m => new CustomReportMark(
            ReportWire.Name(m),
            ReportMarks.Label(m),
            ReportMarks.HasAxis(m),
            [.. ReportMarks.XKinds(m).Select(ReportWire.Name)],
            [.. ReportMarks.Slots(m).Select(s => new CustomReportMarkSlot(s.Key, s.Label, s.Input, s.Accepts, s.Counts, s.Required))]))],
        [.. ReportDerivedOps.All.Select(Op)],
        [.. registry.All.Select(dataset => Describe(dataset, grantedScopes, useMmol))],
        ReportWorkspace.Build(registry, grantedScopes));

    internal static string PanelKindLabel(ReportPanelKind kind) => kind switch
    {
        ReportPanelKind.Chart => "Chart",
        ReportPanelKind.StatTiles => "Statistics tiles",
        ReportPanelKind.Table => "Table",
        ReportPanelKind.Comparison => "Comparison",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>An op's spec with every option that adds output fields switched on, so its marks and fields are the most it offers.</summary>
    internal static ReportDerivedSpec Widest(IReportDerivedOp op) =>
        Activator.CreateInstance(op.SpecType) is TrendSpec trend ? trend with { Confidence = true } : (ReportDerivedSpec)Activator.CreateInstance(op.SpecType)!;

    private static CustomReportOp Op(IReportDerivedOp op)
    {
        var spec = Widest(op);
        return new CustomReportOp(
            op.Name,
            op.Label,
            op.Description,
            op.SourceCount,
            ReportParametersSchema.ForOp(op.SpecType),
            op.XRule,
            op.UnitRule,
            [.. op.SourceXKinds.Select(ReportWire.Name)],
            [.. op.SourceXKinds.Select(k => ReportWire.Name(op.OutputXKind(k)))],
            ["number", "percent"],
            [.. op.Marks(spec).Select(m => new CustomReportOpMark(ReportWire.Name(m), Wire(op.DefaultBindings(m, spec))))]);
    }

    private CustomReportDataset Describe(IReportDataset dataset, IReadOnlySet<string> granted, bool useMmol)
    {
        var permitted = Scope.Satisfies(granted, dataset.RequiredScope);
        var chartFields = dataset.FieldsFor(ReportPanelKind.Chart).Where(f => !f.Companion).ToList();
        return new CustomReportDataset(
            dataset.Name,
            dataset.Label,
            dataset.Description,
            dataset.RequiredScope,
            permitted,
            ReportParametersSchema.For(dataset.ParametersType),
            [.. dataset.Fields.Select(f => f.ResolveUnit(useMmol)).Select(f => new CustomReportField(
                f.Name, f.Label, ReportWire.Name(f.Kind), f.Unit, f.Description, f.Scope,
                permitted && (f.Scope is null || Scope.Satisfies(granted, f.Scope)),
                f.Aggregate is { } aggregate ? ReportWire.Name(aggregate) : null,
                f.Marker is { } marker ? ReportWire.Name(marker) : null,
                f.Setting != ReportSetting.None))],
            [.. dataset.Marks.SelectMany(ReportMarks.XKinds).Distinct()
                .Where(k => chartFields.Any(f => f.AxisKind == k)).Select(ReportWire.Name)],
            [.. dataset.PanelKinds.Select(k => new CustomReportDatasetPanelKind(
                ReportWire.Name(k), [.. dataset.FieldsFor(k).Where(f => !f.Companion).Select(f => f.Name)]))],
            [.. dataset.Marks.Select(m => new CustomReportDatasetMark(ReportWire.Name(m), DefaultBindings(dataset, m, useMmol), Slots(dataset, m)))],
            dataset.DefaultRange.TotalHours,
            dataset.MaxRange.TotalHours,
            $"This data covers at most {ReportSpecValidator.Describe(dataset.MaxRange)}.",
            dataset.MaxPanelsPerReport,
            dataset.SeriesField,
            dataset.SeriesParameter,
            dataset.SeriesField is null ? null : dataset.SeriesCount((ReportDatasetParameters)Activator.CreateInstance(dataset.ParametersType)!));
    }

    /// <summary>The bindings a new layer of <paramref name="dataset"/> drawn as <paramref name="mark"/> starts with, normalised by the validator.</summary>
    internal CustomReportLayerBindings DefaultBindings(IReportDataset dataset, ReportMark mark, bool useMmol)
    {
        var hours = (int)Math.Max(1, Math.Min(dataset.DefaultRange.TotalHours, dataset.MaxRange.TotalHours));
        var spec = new Dictionary<string, object?>
        {
            ["version"] = ReportSpec.CurrentVersion,
            ["panels"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["kind"] = "chart",
                    ["range"] = new { hours },
                    ["tracks"] = new object[]
                    {
                        new { layers = new object[] { new { dataset = new { name = dataset.Name }, mark = ReportWire.Name(mark) } } },
                    },
                },
            },
        };
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol);
        var normalised = validation.Spec?.Panels[0].Tracks?[0].Layers[0].Bindings
            ?? throw new InvalidOperationException(
                $"{dataset.Name} as {ReportWire.Name(mark)} has no default bindings: " + string.Join("; ", validation.Errors));
        return Wire(normalised);
    }

    internal static CustomReportLayerBindings Wire(ReportLayerBindings b) => new(
        b.X, b.Y, b.Edges, b.Series, b.Fit, b.Stacked,
        b.Orientation is { } o ? ReportWire.Name(o) : null,
        b.Start, b.End, b.Category);

    /// <summary>The bindings <paramref name="mark"/> takes over <paramref name="dataset"/>, as the validator checks them.</summary>
    internal static IReadOnlyList<CustomReportBindingSlot> Slots(IReportDataset dataset, ReportMark mark)
    {
        var fields = dataset.FieldsFor(ReportPanelKind.Chart).Where(f => !f.Companion).ToList();
        return [.. ReportMarks.Slots(mark).Select(slot => new CustomReportBindingSlot(
            slot.Key,
            slot.Label,
            slot.Input,
            slot.Input is "field" or "fields" ? [.. fields.Where(f => ReportMarks.Accepts(slot, f)).Select(f => f.Name)] : [],
            slot.Counts,
            slot.Required,
            slot.Key == "orientation" ? Orientations : []))];
    }
}
