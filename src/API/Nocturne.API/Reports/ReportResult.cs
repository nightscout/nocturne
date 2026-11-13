using System.Text.Json.Serialization;
using Nocturne.Core.Models;

namespace Nocturne.API.Reports;

/// <summary>A report run: the normalised spec and each panel's resolved data.</summary>
internal sealed record ReportResult
{
    public required ReportSpec Spec { get; init; }

    /// <summary>The tenant's glucose display unit, <c>mg/dl</c> or <c>mmol</c>.</summary>
    public required string Units { get; init; }

    public const string DefaultGlucoseColorMode = "continuous";

    /// <summary>
    /// How every glucose line in the report is coloured: <c>continuous</c> (the glucose spectrum, the
    /// dashboard's default) or <c>threshold</c> (one colour per range). One value per report, so its
    /// panels never colour glucose two ways.
    /// </summary>
    public string GlucoseColorMode { get; init; } = DefaultGlucoseColorMode;

    /// <summary>
    /// The tenant's IANA time zone, in which the summaries state local times, so the view writes
    /// times in it too; null when the tenant has none and the summaries use UTC.
    /// </summary>
    public string? TimeZone { get; init; }

    public required IReadOnlyList<ReportPanelResult> Panels { get; init; }

    /// <summary>Every panel's summary, joined.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The tenant's regional format (BCP-47), which an image writes its dates and numbers in; null for the default.</summary>
    public string? Locale { get; init; }

    /// <summary>Whether the tenant reads times on a 12-hour clock; null when it chose none.</summary>
    public bool? Hour12 { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportPanelStatus>))]
internal enum ReportPanelStatus
{
    /// <summary>Read and resolved.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Not read: the token lacks the scope of the dataset, or of a field or a source the layer needs.</summary>
    [JsonStringEnumMemberName("not_permitted")]
    NotPermitted,

    /// <summary>Not finished within the report's time limit.</summary>
    [JsonStringEnumMemberName("timed_out")]
    TimedOut,

    /// <summary>The dataset failed; the other panels and layers are still drawn.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

internal sealed record ReportPanelResult
{
    public required string Id { get; init; }

    public string? Title { get; init; }

    /// <summary>The spec's <see cref="ReportPanelSpec.Group"/>, as sent; null for none.</summary>
    public string? Group { get; init; }

    public required ReportPanelKind Kind { get; init; }

    /// <summary>Ok when the panel, or at least one of a chart's layers, was read.</summary>
    public required ReportPanelStatus Status { get; init; }

    /// <summary>The resolved range, Unix milliseconds.</summary>
    public required ReportWindow Window { get; init; }

    /// <summary>A figures, table or comparison panel's dataset; null for a chart.</summary>
    public string? Dataset { get; init; }

    /// <summary>A figures, table or comparison panel's columns; null for a chart.</summary>
    public ReportBindings? Bindings { get; init; }

    /// <summary>A figures, table or comparison panel's fields: the catalogue entries of the columns the rows carry.</summary>
    public IReadOnlyList<ReportField> Fields { get; init; } = [];

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>A chart's horizontal axis, which every layer shares; null for another kind of panel.</summary>
    public ReportXKind? XKind { get; init; }

    /// <summary>A chart's grid as asked for: minutes, 0 for every reading, null for each dataset's own spacing.</summary>
    public int? Grid { get; init; }

    /// <summary>A chart's tracks, top to bottom.</summary>
    public IReadOnlyList<ReportTrackResult> Tracks { get; init; } = [];

    public ReportThresholds? Thresholds { get; init; }

    public required string Summary { get; init; }

    /// <summary>The range was cut to the last 24 hours, all a history-clamped caller reads.</summary>
    public bool Limited { get; init; }

    /// <summary>Fields left out because the token lacks their scope.</summary>
    public IReadOnlyList<ReportOmittedField> Omitted { get; init; } = [];

    public ReportResolution? Resolution { get; init; }

    /// <summary>The web report that shows this panel's data, with its range and parameters; null where there is none.</summary>
    public string? Link { get; init; }

    /// <summary>Every event cohort the panel asked for, with the label that names its filter; empty for other datasets.</summary>
    public IReadOnlyList<ReportCohort> Cohorts { get; init; } = [];

    /// <summary>How the days fell under the panel's day filter; null without one.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportDayCounts? Days { get; init; }

    /// <summary>At most six figures the view prints under the panel, label above value; empty where the panel has none.</summary>
    public IReadOnlyList<ReportFact> Facts { get; init; } = [];

    /// <summary>The one sentence the view prints under the facts; for a panel or a layer that was not drawn, why.</summary>
    public string? Sentence { get; init; }

    /// <summary>The panel's range and how much it read, as one line.</summary>
    public string? Context { get; init; }

    /// <summary>The range is the longest the panel's data covers: no earlier data can be added to it.</summary>
    public bool AtRangeLimit { get; init; }

    /// <summary>The sentence that says how long a range the panel covers, where it is at that limit.</summary>
    public string? RangeLimit { get; init; }

    /// <summary>The query <see cref="ReportLinks"/> opens the panel's web report with, where the dataset gives one.</summary>
    [JsonIgnore]
    public IReadOnlyList<KeyValuePair<string, string?>>? LinkQuery { get; init; }
}

/// <summary>One track of a chart: its layers over the chart's shared horizontal axis.</summary>
/// <param name="Height">The track's share of the chart's height.</param>
internal sealed record ReportTrackResult(string Id, string? Title, double Height, IReadOnlyList<ReportLayerResult> Layers);

/// <summary>Which vertical axis a layer is drawn on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportAxisSide>))]
internal enum ReportAxisSide
{
    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("right")]
    Right,

    /// <summary>Markers and spans: drawn with no axis.</summary>
    [JsonStringEnumMemberName("none")]
    None,
}

/// <summary>One layer of a chart, resolved: its rows, how they are drawn and on which axis.</summary>
internal sealed record ReportLayerResult
{
    /// <summary>The layer's title came from its data (<see cref="ReportDatasetOutput.Name"/>), so an untitled panel it leads takes it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Named { get; init; }

    /// <summary>How the days fell under the layer's day filter; null without one.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportDayCounts? Days { get; init; }

    public required string Id { get; init; }

    /// <summary>The layer's name in the legend: its title, else the dataset's and the fields' names.</summary>
    public required string Title { get; init; }

    public required ReportMark Mark { get; init; }

    public required ReportLayerBindings Bindings { get; init; }

    public required ReportXKind XKind { get; init; }

    /// <summary>The unit of the layer's values on its axis; null for no unit or no axis.</summary>
    public string? Unit { get; init; }

    public required ReportAxisSide Side { get; init; }

    public required ReportPanelStatus Status { get; init; }

    /// <summary>The layer's own range where it gives one; else the panel's.</summary>
    public required ReportWindow Window { get; init; }

    /// <summary>The catalogue entries of the fields the bindings draw and the rows carry, in the tenant's unit.</summary>
    public IReadOnlyList<ReportField> Fields { get; init; } = [];

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>
    /// A series hue the runner gave the layer because an earlier layer on its track takes its fields'
    /// colour; null to draw each field in its own colour (a glucose line in the report's glucose colouring).
    /// </summary>
    public ChartColor? Color { get; init; }

    public IReadOnlyList<ReportFact> Facts { get; init; } = [];

    public string? Sentence { get; init; }

    /// <summary>How the layer's values were folded onto the panel's grid ("Basal rate, last value per 1 day"); null for a layer drawn as read.</summary>
    public string? Context { get; init; }

    /// <summary>The rows were cut to a budget or a read limit, and <see cref="Sentence"/> says how.</summary>
    public bool Limited { get; init; }

    /// <summary>The sentence saying how the rows were cut, which the panel's sentence repeats; null when they were not.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CutNote { get; init; }

    /// <summary>The event groups each source of grouped data offers, by the source's key (a, b), with their words.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ReportCohort>> SourceCohorts { get; init; } = new Dictionary<string, IReadOnlyList<ReportCohort>>();

    /// <summary><see cref="Sentence"/> sets the data against the profile's settings, so it never heads the panel.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SettingsSentence { get; init; }

    public string Summary { get; init; } = string.Empty;

    /// <summary>A band's spans, inner first: the words for each and the fields of its low and high edges.</summary>
    public IReadOnlyList<ReportBandSpan> Spans { get; init; } = [];

    /// <summary>A markers layer's markers, from its rows.</summary>
    public IReadOnlyList<ReportMarker> Markers { get; init; } = [];

    /// <summary>Every event cohort the layer's dataset asked for, with the label that names its filter.</summary>
    public IReadOnlyList<ReportCohort> Cohorts { get; init; } = [];

    /// <summary>Fields left out because the token lacks their scope.</summary>
    public IReadOnlyList<ReportOmittedField> Omitted { get; init; } = [];

    public ReportResolution? Resolution { get; init; }
}

internal sealed record ReportWindow(long From, long To);
