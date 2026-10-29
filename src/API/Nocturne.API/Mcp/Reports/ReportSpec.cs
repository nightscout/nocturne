using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// A report an assistant composes: panels, each a dataset drawn as a primitive. These types are
/// the schema of <c>render_report</c>'s <c>spec</c> argument (<see cref="ReportSpecSchema"/>) and
/// the normalised spec the result returns; <see cref="ReportSpecValidator"/> reads the argument.
/// </summary>
[Description("A report: one or more panels, each one dataset drawn as one chart primitive.")]
internal sealed record ReportSpec
{
    public const int CurrentVersion = 1;
    public const int MaxPanels = 6;

    [Description("The spec format version. Send 1.")]
    [JsonRequired]
    public int Version { get; init; } = CurrentVersion;

    [Description("The report heading. Optional.")]
    public string? Title { get; init; }

    [Description("The time range of every panel that does not give its own. Optional; each dataset has a default.")]
    public ReportRange? Range { get; init; }

    [Description("The panels, top to bottom. 1 to 6.")]
    [JsonRequired]
    public IReadOnlyList<ReportPanelSpec> Panels { get; init; } = [];
}

/// <summary>Either the last <see cref="Hours"/> hours, or <see cref="From"/> to <see cref="To"/>.</summary>
[Description("A time range: either 'hours' (the last N hours to now), or 'from' and 'to' together.")]
internal sealed record ReportRange
{
    [Description("The last N hours, to now. Do not send with 'from' or 'to'.")]
    public int? Hours { get; init; }

    [Description("The start, an ISO 8601 date and time with an offset. Send with 'to'.")]
    public DateTimeOffset? From { get; init; }

    [Description("The end, an ISO 8601 date and time with an offset. Send with 'from'.")]
    public DateTimeOffset? To { get; init; }
}

[Description("One panel: a dataset, the primitive that draws it, and which fields go where.")]
internal sealed record ReportPanelSpec
{
    [Description("A short identifier, unique in the report (letters, digits, '-' and '_'). Optional; p1, p2 ... when missing.")]
    public string? Id { get; init; }

    [Description("The panel heading. Optional.")]
    public string? Title { get; init; }

    [Description("The dataset to read, chosen by 'name', with that dataset's parameters. Call describe_dataset for its fields.")]
    [JsonRequired]
    public ReportDatasetParameters Dataset { get; init; } = null!;

    [Description("How to draw the dataset. Each dataset allows only some primitives; describe_dataset lists them.")]
    [JsonRequired]
    public ReportPrimitive Primitive { get; init; }

    [Description("Which dataset fields the primitive draws. Optional; defaults depend on the primitive.")]
    public ReportBindings? Bindings { get; init; }

    [Description("This panel's time range, in place of the report's. Optional.")]
    public ReportRange? Range { get; init; }
}

[Description(
    "Field bindings. time_series: 'x' (a time field), 'y' (one field, or a list of fields in one unit) "
    + "and optional 'band' (two fields: the lower and upper edge of a shaded range). "
    + "table and stat_tiles: optional 'columns' (the fields to show, in order). glucose_chart: none.")]
internal sealed record ReportBindings
{
    [Description("time_series only: the time field on the horizontal axis. Default 'time'.")]
    public string? X { get; init; }

    [Description("time_series only: the fields drawn as lines. All must have the same unit.")]
    public IReadOnlyList<string>? Y { get; init; }

    [Description("time_series only: two fields, the lower and upper edge of a shaded range, in the unit of 'y'.")]
    public IReadOnlyList<string>? Band { get; init; }

    [Description("table and stat_tiles only: the fields to show, in order. Default: every field the token can read.")]
    public IReadOnlyList<string>? Columns { get; init; }
}

/// <summary>
/// A dataset and its parameters. The <c>name</c> discriminator selects the dataset; each
/// registered <see cref="IReportDataset"/> names its parameter type here, and
/// <c>ReportSpecSchemaTests</c> holds the two lists equal.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "name")]
[JsonDerivedType(typeof(GlucoseSeriesParameters), GlucoseSeriesParameters.Name)]
[JsonDerivedType(typeof(GlucoseStatisticsParameters), GlucoseStatisticsParameters.Name)]
internal abstract record ReportDatasetParameters
{
    [JsonIgnore]
    public abstract string DatasetName { get; }
}

[Description(
    "glucose_series: glucose readings over time, with the target range and, where the token allows, "
    + "insulin on board, carbs on board and the basal rate at each reading.")]
internal sealed record GlucoseSeriesParameters : ReportDatasetParameters
{
    public const string Name = "glucose_series";

    public const int DefaultIntervalMinutes = 5;

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("Minutes between rows, 1 to 60. Default 5. A long range can make it coarser, to keep each series under its point limit.")]
    public int IntervalMinutes { get; init; } = DefaultIntervalMinutes;
}

[Description(
    "glucose_statistics: one row of glucose statistics over the range: time in range, below and above, "
    + "mean, GMI, CV, SD and the number of readings.")]
internal sealed record GlucoseStatisticsParameters : ReportDatasetParameters
{
    public const string Name = "glucose_statistics";

    [JsonIgnore]
    public override string DatasetName => Name;
}

/// <summary>The closed set of chart primitives the view draws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportPrimitive>))]
internal enum ReportPrimitive
{
    [JsonStringEnumMemberName("time_series")]
    TimeSeries,

    [JsonStringEnumMemberName("stat_tiles")]
    StatTiles,

    [JsonStringEnumMemberName("table")]
    Table,

    /// <summary>
    /// The dashboard's glucose chart: readings with insulin, carbs, basal, pump modes, profiles and
    /// the health tracks the token allows. <c>show_glucose</c> draws this.
    /// </summary>
    [JsonStringEnumMemberName("glucose_chart")]
    GlucoseChart,
}

internal static class ReportPrimitives
{
    public static readonly IReadOnlyList<ReportPrimitive> All = Enum.GetValues<ReportPrimitive>();

    public static string WireName(this ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.TimeSeries => "time_series",
        ReportPrimitive.StatTiles => "stat_tiles",
        ReportPrimitive.Table => "table",
        ReportPrimitive.GlucoseChart => "glucose_chart",
        _ => throw new ArgumentOutOfRangeException(nameof(primitive), primitive, null),
    };

    public static ReportPrimitive? Parse(string? name) =>
        All.Cast<ReportPrimitive?>().FirstOrDefault(p => p!.Value.WireName() == name);
}
