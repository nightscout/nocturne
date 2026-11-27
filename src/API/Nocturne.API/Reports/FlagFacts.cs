using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports;

/// <summary>
/// Which dataset fields a flag rule may judge, and the conditions it judges them by. A fact is marked on the field
/// itself (<see cref="ReportField.FlagFact"/>), so the reports catalogue, the evaluator and a dataset added later share
/// one list.
/// </summary>
internal static class FlagFacts
{
    /// <summary>The longest range a period condition reads, in days: the longest a report panel reads.</summary>
    public const int MaxDays = ReportSpec.MaxRangeHours / 24;

    /// <summary>The fields of <paramref name="dataset"/>'s stat tiles row marked as flag facts; none for a dataset with no such row.</summary>
    public static IEnumerable<ReportField> Of(IReportDataset dataset) =>
        dataset.PanelKinds.Contains(ReportPanelKind.StatTiles)
            ? dataset.FieldsFor(ReportPanelKind.StatTiles).Where(f => f.FlagFact is not null)
            : [];

    /// <summary>
    /// Every scope a caller needs to judge <paramref name="fact"/> for a person: the dataset's, the field's own, and for a
    /// period fact <see cref="Scope.ReportsRead"/>, which the Review lens and a custom report also need. A live fact,
    /// like the fixed Flagged set, needs only its dataset's.
    /// </summary>
    public static IReadOnlyList<string> Scopes(IReportDataset dataset, ReportField fact) =>
        [.. new[] { dataset.RequiredScope, fact.Scope, fact.FlagFact?.Kind == ReportFlagFactKind.Period ? Scope.ReportsRead : null }
            .OfType<string>().Distinct(StringComparer.Ordinal)];
}

/// <summary>How a condition compares a person's figure with its value.</summary>
internal enum FlagComparison
{
    /// <summary><c>&gt;</c></summary>
    Above,

    /// <summary><c>&gt;=</c></summary>
    AtLeast,

    /// <summary><c>&lt;</c></summary>
    Below,

    /// <summary><c>&lt;=</c></summary>
    AtMost,
}

/// <summary>A condition as a rule holds it, before it is checked.</summary>
/// <param name="Dataset">The dataset and its parameters, as a report spec's <c>dataset</c> object: <c>{"name": "glucose_statistics"}</c>.</param>
/// <param name="Field">The fact: a field of the dataset's stat tiles row that <see cref="FlagFacts.Of"/> lists.</param>
/// <param name="Days">The last N days the figure covers, 1 to <see cref="FlagFacts.MaxDays"/>; null for a live fact.</param>
/// <param name="Value">The value the figure is compared with, in <paramref name="Unit"/>.</param>
/// <param name="Unit">The unit of <paramref name="Value"/>: the fact's unit, mg/dL for glucose, or null for a fact without one.</param>
internal sealed record FlagCondition(JsonElement Dataset, string Field, int? Days, FlagComparison Comparison, double Value, string? Unit);

/// <summary>A condition checked against the reports engine: the dataset, its parameters and the fact it names.</summary>
internal sealed record FlagFactCondition
{
    public required IReportDataset Dataset { get; init; }

    public required ReportDatasetParameters Parameters { get; init; }

    /// <summary>The fact, its unit resolved to mg/dL.</summary>
    public required ReportField Field { get; init; }

    public required int? Days { get; init; }

    public required FlagComparison Comparison { get; init; }

    public required double Value { get; init; }

    public ReportFlagFact Fact => Field.FlagFact!;

    /// <summary>What one read of the dataset is for this condition: conditions with one read key share it.</summary>
    public string ReadKey =>
        $"{Dataset.Name}|{JsonSerializer.Serialize(Parameters, Parameters.GetType(), ReportSpecValidator.KeyOptions)}|{Days?.ToString(CultureInfo.InvariantCulture)}";

    public bool Matches(double figure) => Comparison switch
    {
        FlagComparison.Above => figure > Value,
        FlagComparison.AtLeast => figure >= Value,
        FlagComparison.Below => figure < Value,
        FlagComparison.AtMost => figure <= Value,
        _ => throw new ArgumentOutOfRangeException(nameof(Comparison), Comparison, null),
    };

    /// <summary>
    /// <paramref name="condition"/> checked as a report panel showing its fact as figures over its range would be
    /// (<see cref="ReportSpecValidator"/>), then as a fact: a field <see cref="FlagFacts.Of"/> lists, a range for a
    /// period fact and none for a live one, and its value in the fact's unit. Errors name <c>dataset</c>,
    /// <c>field</c>, <c>days</c>, <c>unit</c> or <c>value</c>.
    /// </summary>
    public static (FlagFactCondition? Condition, IReadOnlyList<ReportSpecError> Errors) Check(
        FlagCondition condition, ReportSpecValidator validator, IReportDatasetRegistry registry)
    {
        var errors = new List<ReportSpecError>();
        void Error(string path, string message) => errors.Add(new ReportSpecError(path, message));

        if (condition.Dataset.ValueKind == JsonValueKind.Object
            && condition.Dataset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && registry.Find(name.GetString()!) is { } named && !FlagFacts.Of(named).Any())
        {
            Error("dataset.name", $"{named.Label} has no flag facts. Datasets with flag facts: "
                + string.Join(", ", registry.All.Where(d => FlagFacts.Of(d).Any()).Select(d => d.Name)) + ".");
            return (null, errors);
        }

        if (condition.Days is { } days && (days < 1 || days > FlagFacts.MaxDays))
            Error("days", $"Send the last N days as a whole number, 1 to {FlagFacts.MaxDays}.");
        if (!double.IsFinite(condition.Value))
            Error("value", "Send a number.");

        var panel = new JsonObject
        {
            ["kind"] = ReportWire.Name(ReportPanelKind.StatTiles),
            ["dataset"] = condition.Dataset.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(condition.Dataset.GetRawText()),
            ["bindings"] = new JsonObject { ["columns"] = new JsonArray(condition.Field) },
        };
        if (condition.Days is { } range && errors.Count == 0)
            panel["range"] = new JsonObject { ["hours"] = range * 24 };
        var spec = new JsonObject { ["version"] = ReportSpec.CurrentVersion, ["panels"] = new JsonArray(panel) };

        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol: false);
        foreach (var error in validation.Errors)
            Error(PathOf(error.Path), error.Message);
        if (errors.Count > 0)
            return (null, errors);

        var read = validation.Spec!.Panels[0];
        var dataset = registry.Find(read.Dataset!.DatasetName)!;
        var facts = FlagFacts.Of(dataset).ToList();
        if (facts.FirstOrDefault(f => f.Name == condition.Field) is not { } field)
        {
            Error("field", $"'{condition.Field}' is not a flag fact of {dataset.Name}. Flag facts: {string.Join(", ", facts.Select(f => f.Name))}.");
            return (null, errors);
        }

        field = field.ResolveUnit(useMmol: false);
        if (field.FlagFact!.Kind == ReportFlagFactKind.Period && condition.Days is null)
            Error("days", $"{field.Label} is a figure over a period. Send the last N days, 1 to {FlagFacts.MaxDays}.");
        if (field.FlagFact.Kind == ReportFlagFactKind.Live && condition.Days is not null)
            Error("days", $"{field.Label} is a value as it is now and takes no range. Leave 'days' out.");
        if (condition.Unit != field.Unit)
            Error("unit", field.Unit is null
                ? $"{field.Label} has no unit. Leave 'unit' out."
                : $"Send the value of {field.Label} in {field.Unit}.");
        if (errors.Count > 0)
            return (null, errors);

        return (new FlagFactCondition
        {
            Dataset = dataset,
            Parameters = read.Dataset,
            Field = field,
            Days = condition.Days,
            Comparison = condition.Comparison,
            Value = condition.Value,
        }, errors);
    }

    /// <summary>The condition's key that a path into the one-panel spec <see cref="Check"/> validates names.</summary>
    private static string PathOf(string path)
    {
        const string panel = "panels[0]";
        if (!path.StartsWith(panel, StringComparison.Ordinal))
            return "dataset";
        var rest = path[panel.Length..].TrimStart('.');
        return rest switch
        {
            "" or "kind" => "dataset",
            _ when rest.StartsWith("bindings", StringComparison.Ordinal) => "field",
            _ when rest.StartsWith("range", StringComparison.Ordinal) => "days",
            _ => rest,
        };
    }
}
