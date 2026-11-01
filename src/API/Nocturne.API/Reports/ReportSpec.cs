using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Reports.Derived;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.API.Reports;

/// <summary>
/// A custom report: panels, each a chart of tracks of layers or one dataset shown another way. The normalised form of a spec;
/// <see cref="ReportSpecValidator"/> reads the JSON a member sends into it.
/// </summary>
[Description("A report: one or more panels, each a chart of tracks of layers, or one dataset as figures, a table or two periods.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportSpec
{
    public const int CurrentVersion = 1;
    public const int MaxPanels = 6;
    public const int MaxTracks = 4;
    public const int MaxLayers = 8;

    /// <summary>The most dataset reads one report makes, counted after the run cache: a derived layer's sources count.</summary>
    public const int MaxReads = 16;
    public const int MaxTitleLength = 200;
    public const int MaxIdLength = 32;
    public const string IdPattern = "^[A-Za-z0-9_-]{1,32}$";

    /// <summary>The largest spec, as UTF-8 JSON, a request may carry.</summary>
    public const int MaxSpecBytes = 16 * 1024;

    internal const string TooLarge = "The report is too large. A report spec can be at most 16 kB.";

    /// <summary>Whether <paramref name="spec"/>, as its JSON text, is within <see cref="MaxSpecBytes"/>.</summary>
    public static bool WithinLimit(System.Text.Json.JsonElement spec) =>
        System.Text.Encoding.UTF8.GetByteCount(spec.GetRawText()) <= MaxSpecBytes;

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

[Description("One panel: a chart of tracks of layers, or one dataset as figures, a table or two periods side by side.")]
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

    [Description("chart (tracks of layers), stat_tiles, table or comparison.")]
    [JsonRequired]
    public ReportPanelKind Kind { get; init; }

    [Description("This panel's time range, in place of the report's. Optional.")]
    public ReportRange? Range { get; init; }

    [Description(
        "chart only: the minutes each layer's rows are folded into on a time, minutes-from-event or time-of-day axis, by each "
        + "field's own aggregate; 0 for every row, not folded. Optional; each dataset's own spacing when missing.")]
    public int? Grid { get; init; }

    [Description("chart only: the tracks, top to bottom, sharing one horizontal axis. 1 to 4.")]
    public IReadOnlyList<ReportTrackSpec>? Tracks { get; init; }

    [Description("stat_tiles, table and comparison only: the dataset to read, chosen by 'name', with that dataset's parameters.")]
    public ReportDatasetParameters? Dataset { get; init; }

    [Description("stat_tiles, table and comparison only: which fields to show.")]
    public ReportBindings? Bindings { get; init; }
}

/// <summary>The closed set of panel kinds the view draws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportPanelKind>))]
internal enum ReportPanelKind
{
    /// <summary>Tracks of layers over one horizontal axis.</summary>
    [JsonStringEnumMemberName("chart")]
    Chart,

    [JsonStringEnumMemberName("stat_tiles")]
    StatTiles,

    [JsonStringEnumMemberName("table")]
    Table,

    /// <summary>Two periods side by side: each one's time in range bar, then each figure's two values and the change.</summary>
    [JsonStringEnumMemberName("comparison")]
    Comparison,
}

[Description("One track of a chart panel: layers drawn over the panel's horizontal axis, with up to two vertical axes.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportTrackSpec
{
    public const double MinHeight = 0.25;
    public const double MaxHeight = 4;

    /// <summary>The height a track of state spans alone takes when it gives none.</summary>
    public const double LaneHeight = 0.3;

    [Description("A short identifier, unique in the panel. Optional; t1, t2 ... when missing.")]
    [StringLength(ReportSpec.MaxIdLength)]
    [RegularExpression(ReportSpec.IdPattern)]
    public string? Id { get; init; }

    [Description("The track's heading. Optional.")]
    [StringLength(ReportSpec.MaxTitleLength)]
    public string? Title { get; init; }

    [Description("The track's share of the panel's height, 0.25 to 4. Optional; 1, or 0.3 for a lane of state spans.")]
    [Range(MinHeight, MaxHeight)]
    public double? Height { get; init; }

    [Description("The layers, drawn in order. At least one; at most 8 in the panel.")]
    [JsonRequired]
    public IReadOnlyList<ReportLayerSpec> Layers { get; init; } = [];
}

[Description("One layer: a dataset, or a series derived from one or two sources, drawn as one mark.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportLayerSpec
{
    [Description("A short identifier, unique in the panel. Optional; l1, l2 ... when missing.")]
    [StringLength(ReportSpec.MaxIdLength)]
    [RegularExpression(ReportSpec.IdPattern)]
    public string? Id { get; init; }

    [Description("The layer's name in the legend. Optional; the dataset's and the field's names when missing.")]
    [StringLength(ReportSpec.MaxTitleLength)]
    public string? Title { get; init; }

    [Description("The dataset to read, chosen by 'name'. Send this or 'derived'.")]
    public ReportDatasetParameters? Dataset { get; init; }

    [Description("A series the API derives from one or two sources, chosen by 'op'. Send this or 'dataset'.")]
    public ReportDerivedSpec? Derived { get; init; }

    [Description("How the layer is drawn: line, area, band, bars, points, markers or spans. Optional; the first the data allows.")]
    public ReportMark? Mark { get; init; }

    [Description("Which fields the mark draws. Optional; each dataset and mark has defaults.")]
    public ReportLayerBindings? Bindings { get; init; }

    [Description("This layer's range, in place of the panel's: on a time-of-day, minutes-from-event or day axis only. Optional.")]
    public ReportRange? Range { get; init; }

    /// <summary>What the layer lies along; set by the validator.</summary>
    [JsonIgnore]
    public ReportXKind XKind { get; init; }

    /// <summary>The unit of the layer's values on its vertical axis, null for none; set by the validator.</summary>
    [JsonIgnore]
    public string? Unit { get; init; }

    /// <summary>Whether the layer takes a vertical axis (markers and spans do not); set by the validator.</summary>
    [JsonIgnore]
    public bool HasAxis { get; init; }
}

[Description(
    "A layer's field bindings. line, area and points: 'x' and 'y' (fields in one unit); points also 'fit' (a line through them). "
    + "band: 'x', 'edges' (two fields, or four: outer low, inner low, inner high, outer high), optional 'y' (one middle line) "
    + "and 'series' (a category; one set of bands per value). bars: 'x', 'y', 'stacked', 'orientation'. markers: 'x' and 'y' "
    + "(marker fields). spans: 'start', 'end' and 'category'.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportLayerBindings
{
    [Description("The field along the horizontal axis.")]
    public string? X { get; init; }

    [Description("The fields drawn, all in one unit.")]
    public IReadOnlyList<string>? Y { get; init; }

    [Description("band only: two or four edge fields, in the unit of 'y'.")]
    public IReadOnlyList<string>? Edges { get; init; }

    [Description("band and line only: a category field; one set per value.")]
    public string? Series { get; init; }

    [Description("points only: a field drawn as a line through the points.")]
    public string? Fit { get; init; }

    [Description("bars only: true to stack the 'y' fields.")]
    public bool? Stacked { get; init; }

    [Description("bars only: vertical (the default) or horizontal.")]
    public ReportBarOrientation? Orientation { get; init; }

    [Description("spans only: the field where a span starts.")]
    public string? Start { get; init; }

    [Description("spans only: the field where a span ends.")]
    public string? End { get; init; }

    [Description("spans only: the category field that names each span.")]
    public string? Category { get; init; }
}

[Description("stat_tiles, table and comparison: 'columns', the fields to show, in order. Optional; every field the token can read.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportBindings
{
    [Description("The fields to show, in order. Default: every field the token can read.")]
    public IReadOnlyList<string>? Columns { get; init; }
}

/// <summary>Which way a bars layer's bars run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportBarOrientation>))]
internal enum ReportBarOrientation
{
    [JsonStringEnumMemberName("vertical")]
    Vertical,

    [JsonStringEnumMemberName("horizontal")]
    Horizontal,
}

/// <summary>The closed set of marks a layer is drawn as.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportMark>))]
internal enum ReportMark
{
    [JsonStringEnumMemberName("line")]
    Line,

    /// <summary>A line with a fill to zero.</summary>
    [JsonStringEnumMemberName("area")]
    Area,

    /// <summary>A shaded range between two edges, or two ranges between four, with an optional middle line.</summary>
    [JsonStringEnumMemberName("band")]
    Band,

    [JsonStringEnumMemberName("bars")]
    Bars,

    [JsonStringEnumMemberName("points")]
    Points,

    /// <summary>The dashboard's bolus, carb, BG check and injection glyphs at their amounts; no axis.</summary>
    [JsonStringEnumMemberName("markers")]
    Markers,

    /// <summary>State spans in a lane, each from its start to its end, named by its category; no axis.</summary>
    [JsonStringEnumMemberName("spans")]
    Spans,
}

/// <summary>What a chart panel's horizontal axis holds; every layer on the panel shares it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportXKind>))]
internal enum ReportXKind
{
    /// <summary>An instant, Unix milliseconds.</summary>
    [JsonStringEnumMemberName("time")]
    Time,

    /// <summary>Minutes from an event's anchor.</summary>
    [JsonStringEnumMemberName("offset")]
    Offset,

    /// <summary>Minutes after local midnight.</summary>
    [JsonStringEnumMemberName("time_of_day")]
    TimeOfDay,

    /// <summary>A local calendar day, its midnight in Unix milliseconds.</summary>
    [JsonStringEnumMemberName("day")]
    Day,

    [JsonStringEnumMemberName("category")]
    Category,

    /// <summary>A value of a scatter's first source, in its unit.</summary>
    [JsonStringEnumMemberName("value")]
    Value,

    /// <summary>Minutes one source is shifted against the other.</summary>
    [JsonStringEnumMemberName("lag")]
    Lag,
}

/// <summary>Enum values as a spec writes them.</summary>
internal static class ReportWire
{
    public static string Name<T>(T value) where T : struct, Enum =>
        JsonSerializer.Serialize(value).Trim('"');

    public static T? Parse<T>(string? name) where T : struct, Enum =>
        name is null ? null : Enum.GetValues<T>().Cast<T?>().FirstOrDefault(v => Name(v!.Value) == name);

    public static string List<T>(IEnumerable<T> values) where T : struct, Enum => string.Join(", ", values.Select(Name));
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
[JsonDerivedType(typeof(PeriodComparisonParameters), PeriodComparisonParameters.Name)]
[JsonDerivedType(typeof(StateSpansParameters), StateSpansParameters.Name)]
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

    [JsonIgnore]
    public override string DatasetName => Name;
}

/// <summary>A treatment marker the dashboard chart draws, which a markers layer draws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportMarkerKind>))]
internal enum ReportMarkerKind
{
    [JsonStringEnumMemberName("bolus")]
    Bolus,

    [JsonStringEnumMemberName("carbs")]
    Carbs,

    [JsonStringEnumMemberName("bg_check")]
    BgCheck,

    [JsonStringEnumMemberName("basal_injection")]
    BasalInjection,
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
