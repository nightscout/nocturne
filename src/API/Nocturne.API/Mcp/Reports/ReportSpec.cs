using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Nocturne.API.Controllers.V4.Base;
using System.Text.Json.Serialization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// A report an assistant composes: panels, each a dataset drawn as a primitive. These types are
/// the schema of <c>render_report</c>'s <c>spec</c> argument (<see cref="ReportSpecSchema"/>) and
/// the normalised spec the result returns; <see cref="ReportSpecValidator"/> reads the argument.
/// </summary>
[Description("A report: one or more panels, each one dataset drawn as one chart primitive.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportSpec
{
    public const int CurrentVersion = 1;
    public const int MaxPanels = 6;
    public const int MaxTitleLength = 200;
    public const int MaxIdLength = 32;
    public const string IdPattern = "^[A-Za-z0-9_-]{1,32}$";

    /// <summary>The longest range any dataset covers; each dataset may allow less.</summary>
    public const int MaxRangeHours = V4ReadLimits.MaxAnalyticsSpanDays * 24;

    [Description("The spec format version. Send 1.")]
    [JsonRequired]
    public int Version { get; init; } = CurrentVersion;

    [Description("The report heading. Optional.")]
    [StringLength(MaxTitleLength)]
    public string? Title { get; init; }

    [Description("The time range of every panel that does not give its own. Optional; each dataset has a default.")]
    public ReportRange? Range { get; init; }

    [Description("The panels, top to bottom. 1 to 6.")]
    [JsonRequired]
    [Length(1, MaxPanels)]
    public IReadOnlyList<ReportPanelSpec> Panels { get; init; } = [];
}

/// <summary>Either the last <see cref="Hours"/> hours, or <see cref="From"/> to <see cref="To"/>.</summary>
[Description("A time range: either 'hours' (the last N hours to now), or 'from' and 'to' together.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportRange
{
    [Description("The last N hours, to now. Do not send with 'from' or 'to'.")]
    [Range(1, ReportSpec.MaxRangeHours)]
    public int? Hours { get; init; }

    [Description("The start, an ISO 8601 date and time with an offset. Send with 'to'.")]
    public DateTimeOffset? From { get; init; }

    [Description("The end, an ISO 8601 date and time with an offset. Send with 'from'.")]
    public DateTimeOffset? To { get; init; }
}

[Description("One panel: a dataset, the primitive that draws it, and which fields go where.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportPanelSpec
{
    [Description("A short identifier, unique in the report (letters, digits, '-' and '_'). Optional; p1, p2 ... when missing.")]
    [StringLength(ReportSpec.MaxIdLength)]
    [RegularExpression(ReportSpec.IdPattern)]
    public string? Id { get; init; }

    [Description("The panel heading. Optional.")]
    [StringLength(ReportSpec.MaxTitleLength)]
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
    "Field bindings. time_series: 'x' (a time, time-of-day or minutes-from-event field), 'y' (a list of fields in one unit) "
    + "and optional 'band' (two fields: the lower and upper edge of a shaded range). "
    + "range_bands: 'x', 'y' (one field: the median line), 'band' (two fields, or four: outer low, inner low, inner high, "
    + "outer high) and optional 'series' (a category field; one set of bands per value). Optional; each dataset has defaults. "
    + "bar: 'x' (a category field), 'y' (one or more number fields in one unit) and optional 'stacked'. "
    + "table and stat_tiles: optional 'columns' (the fields to show, in order). glucose_chart: none for glucose_series; "
    + "for event_response as range_bands, defaulting to the median and the middle half by cohort.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportBindings
{
    [Description("time_series and range_bands: the field on the horizontal axis: a time, time-of-day or minutes-from-event field. bar: a category field, one bar or group per value.")]
    public string? X { get; init; }

    [Description("time_series: the fields drawn as lines, all in one unit. range_bands: one field, the median line. bar: the fields drawn as bars, all in one unit.")]
    public IReadOnlyList<string>? Y { get; init; }

    [Description(
        "time_series: two fields, the lower and upper edge of a shaded range, in the unit of 'y'. range_bands: two or four "
        + "fields in the unit of 'y': the outer band's low edge, then (with four) the inner band's low and high edges, then the outer band's high edge.")]
    public IReadOnlyList<string>? Band { get; init; }

    [Description("range_bands only: a category field; each of its values is drawn as its own bands and line.")]
    public string? Series { get; init; }

    [Description("table and stat_tiles only: the fields to show, in order. Default: every field the token can read.")]
    public IReadOnlyList<string>? Columns { get; init; }

    [Description("bar only: true to stack the 'y' fields in one bar per category, false (the default) to draw them side by side.")]
    public bool? Stacked { get; init; }
}

/// <summary>
/// A dataset and its parameters. The <c>name</c> discriminator selects the dataset; each
/// registered <see cref="IReportDataset"/> names its parameter type here, and
/// <c>ReportSpecSchemaTests</c> holds the two lists equal.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "name")]
[JsonDerivedType(typeof(GlucoseSeriesParameters), GlucoseSeriesParameters.Name)]
[JsonDerivedType(typeof(GlucoseStatisticsParameters), GlucoseStatisticsParameters.Name)]
[JsonDerivedType(typeof(EventResponseParameters), EventResponseParameters.Name)]
[JsonDerivedType(typeof(SettingsEvidenceParameters), SettingsEvidenceParameters.Name)]
[JsonDerivedType(typeof(DailyProfileParameters), DailyProfileParameters.Name)]
[JsonDerivedType(typeof(GlucoseDistributionByHourParameters), GlucoseDistributionByHourParameters.Name)]
[JsonDerivedType(typeof(TreatmentsParameters), TreatmentsParameters.Name)]
[JsonDerivedType(typeof(BasalDeliveryParameters), BasalDeliveryParameters.Name)]
[JsonDerivedType(typeof(DeviceStatusParameters), DeviceStatusParameters.Name)]
[JsonDerivedType(typeof(GlucoseDistributionParameters), GlucoseDistributionParameters.Name)]
[JsonDerivedType(typeof(DailySummaryParameters), DailySummaryParameters.Name)]
internal abstract record ReportDatasetParameters
{
    [JsonIgnore]
    public abstract string DatasetName { get; }
}

[Description(
    "glucose_series: glucose readings over time, with the target range and, where the token allows, "
    + "insulin on board, carbs on board and the basal rate at each reading.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlucoseSeriesParameters : ReportDatasetParameters
{
    public const string Name = "glucose_series";

    public const int DefaultIntervalMinutes = 5;

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("Minutes between rows, 1 to 60. Default 5. A long range can make it coarser, to keep each series under its point limit.")]
    [Range(1, 60)]
    public int IntervalMinutes { get; init; } = DefaultIntervalMinutes;

    [Description(
        "time_series only: treatment markers to draw on the glucose line, 'bolus' and/or 'carbs'. Optional. Needs "
        + "treatments.read; without it the markers are left out and the summary says so. A range of at most 72 hours.")]
    public IReadOnlyList<ReportMarkerKind>? Markers { get; init; }
}

/// <summary>A treatment marker the dashboard chart draws, which a panel can draw on a time series.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportMarkerKind>))]
internal enum ReportMarkerKind
{
    [JsonStringEnumMemberName("bolus")]
    Bolus,

    [JsonStringEnumMemberName("carbs")]
    Carbs,
}

[Description(
    "glucose_statistics: one row of glucose statistics over the range: time in range, below and above, "
    + "mean, GMI, CV, SD and the number of readings.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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
    /// the health tracks the token allows. <c>show_glucose</c> draws this. Over event cohorts
    /// (<c>event_response</c>), each cohort's band and median over single events' glucose.
    /// </summary>
    [JsonStringEnumMemberName("glucose_chart")]
    GlucoseChart,

    /// <summary>Percentile bands (outer and inner) under a median line, one set per series, over an axis of time of day or minutes from an event.</summary>
    [JsonStringEnumMemberName("range_bands")]
    RangeBands,

    /// <summary>Bars over a category axis, one or more fields per category, side by side or stacked.</summary>
    [JsonStringEnumMemberName("bar")]
    Bar,
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
        ReportPrimitive.RangeBands => "range_bands",
        ReportPrimitive.Bar => "bar",
        _ => throw new ArgumentOutOfRangeException(nameof(primitive), primitive, null),
    };

    public static ReportPrimitive? Parse(string? name) =>
        All.Cast<ReportPrimitive?>().FirstOrDefault(p => p!.Value.WireName() == name);
}
