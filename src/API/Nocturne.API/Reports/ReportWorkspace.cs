using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Nocturne.API.Authorization;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Reports.Derived;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Reports;

/// <summary>
/// The report workspace's vocabulary: the signals it places, the lenses it draws them through, and for each signal
/// under each lens the dataset, field, marks and bindings that draw it, with what a derived layer may do to it. Ids
/// only; the web's translation layer gives the words. What the engine states elsewhere (limits, window choices,
/// which figures apply to which events, mark slots, parameter defaults) is referenced, not restated.
/// </summary>
/// <param name="Lenses">The lenses, in the order the workspace offers them: timeline, typical_day, event, daily, against.</param>
/// <param name="CombineOps">The derived ops that combine two signals of one lens (difference, ratio).</param>
/// <param name="AgainstGridMinutes">The grid, in minutes, a scatter or lag correlation of two signals pairs its sources on.</param>
/// <param name="TimelineGrids">
/// The grids, in minutes, a smoothed timeline is drawn on: the finest that is not finer than the range allows
/// (<paramref name="MaxTimeSlots"/>), with each smoothing window rounded up to a whole number of its slots.
/// </param>
/// <param name="MaxTimeSlots">The most slots a time axis holds; a longer range coarsens its grid to fit.</param>
/// <param name="TimelineRawHours">
/// The longest timeline drawn reading by reading. A longer one is drawn on its timeline grid (<paramref name="TimelineGrids"/>),
/// each slot holding its readings' mean, sum or last value, which the layer says ("mean per 30 minutes"): drawn raw, the
/// series would keep one reading of each slot its point budget allows, and a range of weeks would show a field of spikes.
/// </param>
public sealed record CustomReportWorkspace(
    [property: Required] IReadOnlyList<string> Lenses,
    [property: Required] IReadOnlyList<CustomReportWorkspaceFamily> Families,
    [property: Required] IReadOnlyList<CustomReportWorkspaceSection> Sections,
    [property: Required] IReadOnlyList<CustomReportWorkspaceSignal> Signals,
    [property: Required] IReadOnlyList<CustomReportWorkspaceCell> Cells,
    [property: Required] IReadOnlyList<string> AnchorGroups,
    [property: Required] IReadOnlyList<CustomReportWorkspaceAnchor> Anchors,
    [property: Required] IReadOnlyList<CustomReportWorkspaceDayKind> DayKinds,
    [property: Required] IReadOnlyList<CustomReportWorkspaceDayGroupPreset> DayGroupPresets,
    [property: Required] IReadOnlyList<CustomReportWorkspaceLeaveOut> LeaveOuts,
    [property: Required] IReadOnlyList<CustomReportWorkspaceRangePreset> RangePresets,
    [property: Required] int DefaultReportHours,
    [property: Required] IReadOnlyList<string> CombineOps,
    [property: Required] int AgainstGridMinutes,
    [property: Required] IReadOnlyList<int> TimelineGrids,
    [property: Required] int MaxTimeSlots,
    [property: Required] int TimelineRawHours,
    [property: Required] CustomReportWorkspaceEvents Events,
    [property: Required] CustomReportWorkspaceSources Sources);

/// <summary>A family of signals and the share of a chart's height its lane takes.</summary>
public sealed record CustomReportWorkspaceFamily([property: Required] string Id, [property: Required] double LaneHeight);

/// <summary>
/// A section of the signal catalogue and its groups, in the order the catalogue lists them. A section may have no
/// signals; the catalogue shows only the ones that do.
/// </summary>
public sealed record CustomReportWorkspaceSection([property: Required] string Id, [property: Required] IReadOnlyList<string> Groups);

/// <summary>
/// A signal: the lane family it is drawn in, the lane it is drawn in when not its family's, its theme color token,
/// and where the catalogue lists it, independent of any lens.
/// </summary>
/// <param name="Family">The family whose lane draws it: a chart's lanes and their heights (<see cref="CustomReportWorkspace.Families"/>).</param>
/// <param name="Section">The catalogue section it is listed under (<see cref="CustomReportWorkspace.Sections"/>).</param>
/// <param name="Group">Its group within the section.</param>
/// <param name="Unit">The unit of its first cell that has one, as the cells write it (<c>glucose</c> for the display unit); null for none.</param>
/// <param name="Lenses">The lenses that draw it, in lens order.</param>
/// <param name="MaxRangeHours">The longest range any lens draws it over: the most its datasets cover.</param>
/// <param name="SearchKeys">Ids of the words, besides its name, a search finds it by (abbreviations and other names); the web gives the words.</param>
/// <param name="Permitted">Whether the caller holds the scopes of at least one of its cells.</param>
public sealed record CustomReportWorkspaceSignal(
    [property: Required] string Id,
    [property: Required] string Family,
    string? Lane,
    [property: Required] string Color,
    [property: Required] bool Permitted,
    [property: Required] string Section,
    [property: Required] string Group,
    string? Unit,
    [property: Required] IReadOnlyList<string> Lenses,
    [property: Required] double MaxRangeHours,
    [property: Required] IReadOnlyList<string> SearchKeys);

/// <summary>How one signal is drawn under one lens.</summary>
/// <param name="Parameters">The dataset parameters the cell always sends, beside its name; null for none.</param>
/// <param name="Field">The field a derived layer (smoothing, trend, combination, scatter, lag) reads; null where none may.</param>
/// <param name="Unit">The unit of the cell's lead field as the catalogue's fields write it (<c>glucose</c> for the display unit); null for none.</param>
/// <param name="Marks">The ways it is drawn, the default first; empty under <c>against</c>, where the signal is only a source.</param>
/// <param name="FromStartField">Under <c>event</c>: the field of the change from each event's start value.</param>
/// <param name="FromStartMarks">Under <c>event</c>: the marks that draw that change.</param>
/// <param name="Smooth">The rolling-mean and mean-and-standard-deviation windows, in minutes, its axis takes; empty when none apply.</param>
/// <param name="Trend">Whether a trend line may be drawn through it.</param>
/// <param name="Combine">Whether it may be one source of a <see cref="CustomReportWorkspace.CombineOps"/> op.</param>
/// <param name="Weekdays">The dataset takes <c>dayOfWeek</c>, so a group of days narrows it. Read off its parameter schema.</param>
/// <param name="Happened">The dataset takes <c>days</c>: only the days that had something, or all other days. Read off its parameter schema.</param>
/// <param name="LeaveOut">The dataset takes the leave-out parameters (<see cref="CustomReportWorkspace.LeaveOuts"/>). Read off its parameter schema.</param>
/// <param name="SpanKind">A kind of state span (also in <paramref name="Parameters"/>): a lane draws every placed kind in one layer.</param>
/// <param name="Scopes">The read scopes the cell needs: its dataset's and its fields'.</param>
/// <param name="MaxRangeHours">The longest range the cell's dataset covers.</param>
public sealed record CustomReportWorkspaceCell(
    [property: Required] string Lens,
    [property: Required] string Signal,
    [property: Required] string Dataset,
    JsonElement? Parameters,
    string? Field,
    string? Unit,
    [property: Required] IReadOnlyList<CustomReportWorkspaceMark> Marks,
    string? FromStartField,
    IReadOnlyList<CustomReportWorkspaceMark>? FromStartMarks,
    [property: Required] IReadOnlyList<int> Smooth,
    [property: Required] bool Trend,
    [property: Required] bool Combine,
    [property: Required] bool Weekdays,
    [property: Required] bool Happened,
    [property: Required] bool LeaveOut,
    string? SpanKind,
    [property: Required] IReadOnlyList<string> Scopes,
    [property: Required] bool Permitted,
    [property: Required] double MaxRangeHours);

/// <summary>One way a cell is drawn: the spec's mark, the word id the web offers it by, and its bindings.</summary>
/// <param name="Word">line, lines, area, points, bars, stacked, medianBand, band, range, percentiles, median, markers or spans.</param>
public sealed record CustomReportWorkspaceMark(
    [property: Required] string Mark,
    [property: Required] string Word,
    [property: Required] CustomReportLayerBindings Bindings);

/// <summary>An event anchor as the workspace lists it.</summary>
/// <param name="MealNarrowing">Grams, foods and a food category narrow its groups: meals only, as the validator refuses them elsewhere.</param>
/// <param name="Corroboration">Whether <c>requireCorroboration</c> changes which of its events are found.</param>
/// <param name="Scopes">The read scopes its events need, as the scope guard requires them.</param>
public sealed record CustomReportWorkspaceAnchor(
    [property: Required] string Id,
    [property: Required] string Group,
    [property: Required] bool MealNarrowing,
    [property: Required] bool Corroboration,
    [property: Required] IReadOnlyList<string> Scopes,
    [property: Required] bool Permitted);

/// <summary>What a day can have had, with the read scopes a day filter of it needs beyond its dataset's.</summary>
public sealed record CustomReportWorkspaceDayKind(
    [property: Required] string Id,
    [property: Required] IReadOnlyList<string> Scopes,
    [property: Required] bool Permitted);

/// <summary>A preset of weekday groups.</summary>
public sealed record CustomReportWorkspaceDayGroupPreset(
    [property: Required] string Id,
    [property: Required] IReadOnlyList<IReadOnlyList<string>> Groups);

/// <summary>Something an analysis may leave out, by the dataset parameter that does it; its default is the parameter's schema default.</summary>
/// <param name="Scopes">The read scopes the parameter needs, as its schema's <c>x-scope</c> says.</param>
public sealed record CustomReportWorkspaceLeaveOut(
    [property: Required] string Id,
    [property: Required] string Parameter,
    [property: Required] IReadOnlyList<string> Scopes,
    [property: Required] bool Permitted);

/// <summary>A report range preset, in hours.</summary>
public sealed record CustomReportWorkspaceRangePreset([property: Required] string Id, [property: Required] int Hours);

/// <summary>
/// How the event lens shows events: the dataset of each show, the figures, what each event can lie along, and the
/// axes of when events happen. Window choices are the event datasets' <c>minutesBefore</c> and <c>minutesAfter</c>
/// schema; which figure applies to which anchor is their <c>x-eventFigures</c>.
/// </summary>
/// <param name="Shows">Each show (curve, each, spread, when) and its dataset.</param>
/// <param name="NoLineAlong">The axes no line through the points is drawn along.</param>
public sealed record CustomReportWorkspaceEvents(
    [property: Required] IReadOnlyList<CustomReportWorkspaceEventShow> Shows,
    [property: Required] IReadOnlyList<CustomReportWorkspaceOutcome> Outcomes,
    [property: Required] IReadOnlyList<string> Along,
    [property: Required] IReadOnlyList<string> NoLineAlong,
    [property: Required] IReadOnlyList<string> WhenAxes);

public sealed record CustomReportWorkspaceEventShow([property: Required] string Id, [property: Required] string Dataset);

/// <summary>A figure of each event.</summary>
/// <param name="SpreadOnly">Drawn only as a spread: as each event it is an axis, not a point.</param>
public sealed record CustomReportWorkspaceOutcome([property: Required] string Id, [property: Required] bool SpreadOnly);

/// <summary>The datasets a block of figures, a table and a comparison read, in the order the workspace lists them.</summary>
public sealed record CustomReportWorkspaceSources(
    [property: Required] IReadOnlyList<string> Figures,
    [property: Required] IReadOnlyList<string> Tables,
    [property: Required] string Comparison);

/// <summary>The workspace vocabulary (<see cref="CustomReportWorkspace"/>), built against the registered datasets.</summary>
internal static class ReportWorkspace
{
    public const string Timeline = "timeline";
    public const string TypicalDay = "typical_day";
    public const string Event = "event";
    public const string Daily = "daily";
    public const string Against = "against";

    public const int AgainstGridMinutes = 15;
    public const int DefaultReportHours = 336;
    public static readonly int[] TimelineGrids = [5, 10, 15, 20, 30, 60, 120, 240];
    public const int TimelineRawHours = 336;

    /// <summary>One cell as the table writes it, before the registry resolves its scopes and unit.</summary>
    internal sealed record CellSpec(
        string Lens,
        string Signal,
        string Dataset,
        IReadOnlyDictionary<string, object>? Parameters,
        string? Field,
        IReadOnlyList<CustomReportWorkspaceMark> Marks,
        int[] Smooth,
        bool Trend,
        bool Combine = false,
        string? SpanKind = null,
        string? FromStartField = null,
        IReadOnlyList<CustomReportWorkspaceMark>? FromStartMarks = null,
        IReadOnlyList<string>? Scopes = null);

    private static CustomReportLayerBindings Bind(
        string? x = null, IReadOnlyList<string>? y = null, IReadOnlyList<string>? edges = null, string? series = null,
        bool? stacked = null, string? start = null, string? end = null, string? category = null) =>
        new(x, y, edges, series, null, stacked, null, start, end, category);

    private static CustomReportWorkspaceMark Line(string x, string y, string word = "line") => new("line", word, Bind(x, [y]));
    private static CustomReportWorkspaceMark Lines(string x, string[] y) => new("line", "lines", Bind(x, y));
    private static CustomReportWorkspaceMark Area(string x, string y) => new("area", "area", Bind(x, [y]));
    private static CustomReportWorkspaceMark Points(string x, params string[] y) => new("points", "points", Bind(x, y));
    private static CustomReportWorkspaceMark Bars(string x, string y) => new("bars", "bars", Bind(x, [y]));
    private static CustomReportWorkspaceMark Stacked(string x, string[] y) => new("bars", "stacked", Bind(x, y, stacked: true));
    private static CustomReportWorkspaceMark Band(string x, string mid, string low, string high, string? series = null) =>
        new("band", "medianBand", Bind(x, [mid], [low, high], series));
    private static CustomReportWorkspaceMark Edges(string x, string low, string high, string word, string? series = null) =>
        new("band", word, Bind(x, edges: [low, high], series: series));
    private static CustomReportWorkspaceMark SeriesLine(string y) => new("line", "median", Bind("minute", [y], series: "series"));
    private static CustomReportWorkspaceMark Markers(string y) => new("markers", "markers", Bind("time", [y]));
    private static readonly CustomReportWorkspaceMark Spans = new("spans", "spans", Bind(start: "start", end: "end", category: "kind"));

    private static readonly int[] TimeSmooth = [30, 60, 120, 240];
    private static readonly int[] FoldSmooth = [30, 60, 120];
    private static readonly int[] DaySmooth = [4320, 10080];

    private static CellSpec Series(string signal, string field) =>
        new(Timeline, signal, GlucoseSeriesParameters.Name, null, field, [Line("time", field), Area("time", field)], TimeSmooth, true, Combine: true);

    private static CellSpec Treatments(string signal, ReportTreatmentType type, string field) =>
        new(Timeline, signal, TreatmentsParameters.Name, new Dictionary<string, object> { ["types"] = new[] { ReportWire.Name(type) } }, field,
            [Markers(field), Bars("time", field)], [], false);

    private static CellSpec Device(string signal, string field) =>
        new(Timeline, signal, DeviceStatusParameters.Name, null, field, [Line("time", field), Points("time", field)], TimeSmooth, true);

    private static CellSpec Span(string signal, ReportSpanKind kind) =>
        new(Timeline, signal, StateSpansParameters.Name, new Dictionary<string, object> { ["kinds"] = new[] { ReportWire.Name(kind) } }, null,
            [Spans], [], false, SpanKind: ReportWire.Name(kind), Scopes: [StateSpansDataset.ScopeOf(kind)]);

    private static CellSpec Profile(string signal, string? field, params CustomReportWorkspaceMark[] marks) =>
        new(TypicalDay, signal, DailyProfileParameters.Name, null, field,
            marks.Length > 0 ? marks : [Line("minuteOfDay", field!), Area("minuteOfDay", field!)],
            FoldSmooth, true, Combine: true);

    private static CellSpec Setting(string signal, SettingsEvidenceSetting setting, string observed, string inProfile) =>
        new(TypicalDay, signal, SettingsEvidenceParameters.Name, new Dictionary<string, object> { ["setting"] = ReportWire.Name(setting) }, observed,
            [Lines("startMinute", [observed, inProfile]), Points("startMinute", observed, inProfile)], [], false);

    private static CellSpec Day(string signal, string field, bool barsFirst) =>
        new(Daily, signal, DailySummaryParameters.Name, null, field,
            barsFirst ? [Bars("day", field), Line("day", field), Points("day", field)] : [Line("day", field), Points("day", field), Bars("day", field)],
            DaySmooth, true, Combine: true);

    private static CellSpec EventCell(string signal, string? field, params CustomReportWorkspaceMark[] marks) =>
        new(Event, signal, EventResponseParameters.Name, null, field, marks.Length > 0 ? marks : [SeriesLine(field!)], [], false);

    private static CellSpec AgainstCell(string signal, string dataset, string field) => new(Against, signal, dataset, null, field, [], [], false);

    /// <summary>The signal x lens table. A signal missing from a lens is not drawn under it.</summary>
    internal static readonly IReadOnlyList<CellSpec> Table =
    [
        Series("glucose", "glucose") with { Marks = [Line("time", "glucose"), Points("time", "glucose")] },
        new(Timeline, "target", GlucoseSeriesParameters.Name, null, null, [Edges("time", "targetLow", "targetHigh", "band")], [], false),
        Series("iob", "iob"),
        Series("cob", "cob"),
        new(Timeline, "basal", BasalDeliveryParameters.Name, null, "delivered", [Area("time", "delivered"), Line("time", "delivered")], TimeSmooth, true, Combine: true),
        new(Timeline, "basalScheduled", BasalDeliveryParameters.Name, null, "scheduled", [Line("time", "scheduled"), Area("time", "scheduled")], [], false),
        Treatments("boluses", ReportTreatmentType.Bolus, "insulin"),
        Treatments("injections", ReportTreatmentType.BasalInjection, "injectionUnits"),
        Treatments("carbs", ReportTreatmentType.Carbs, "carbs"),
        Treatments("bgChecks", ReportTreatmentType.BgCheck, "bgCheck") with { Marks = [Markers("bgCheck"), Points("time", "bgCheck")] },
        Device("reservoir", "reservoir"),
        Device("pumpBattery", "pumpBattery"),
        Device("uploaderBattery", "uploaderBattery"),
        new(Timeline, "heartRate", HeartRateParameters.Name, null, "bpm",
            [Line("time", "bpm"), new("band", "range", Bind("time", ["bpm"], ["bpmMin", "bpmMax"]))], TimeSmooth, true, Combine: true),
        new(Timeline, "steps", StepsParameters.Name, null, "steps", [Bars("time", "steps"), Line("time", "steps")], [], false),
        new(Timeline, "sleep", SleepParameters.Name, null, null, [Spans], [], false),
        Span("pumpModes", ReportSpanKind.PumpMode),
        Span("profiles", ReportSpanKind.Profile),
        Span("overrides", ReportSpanKind.Override),
        Span("activities", ReportSpanKind.Activity),
        Span("tempBasals", ReportSpanKind.TempBasal),

        Profile("glucose", "glucoseP50",
            Band("minuteOfDay", "glucoseP50", "glucoseP25", "glucoseP75"), Line("minuteOfDay", "glucoseP50", "median")),
        Profile("target", null, Edges("minuteOfDay", "targetLow", "targetHigh", "band")) with { Smooth = [], Trend = false, Combine = false },
        Profile("glucoseWide", null, Edges("minuteOfDay", "glucoseP10", "glucoseP90", "percentiles")) with { Smooth = [], Trend = false, Combine = false },
        Profile("readings", "readings") with { Smooth = [] },
        new(TypicalDay, "ranges", GlucoseDistributionByHourParameters.Name, null, null,
            [Stacked("hour", ["veryLow", "low", "tightRange", "aboveTightRange", "high", "veryHigh"])], [], false),
        new(TypicalDay, "mean", GlucoseDistributionByHourParameters.Name, null, "mean", [Line("hour", "mean"), Bars("hour", "mean")], [], true),
        Profile("iob", "iob"),
        Profile("cob", "cob"),
        Profile("basal", "deliveredBasal"),
        Profile("basalScheduled", "scheduledBasal") with { Smooth = [], Trend = false, Combine = false },
        Profile("netLoop", "netLoopCorrection", Area("minuteOfDay", "netLoopCorrection"), Line("minuteOfDay", "netLoopCorrection"))
            with { Smooth = [], Trend = false, Combine = false },
        Setting("settingBasal", SettingsEvidenceSetting.Basal, "basalObserved", "basalProfile"),
        Setting("settingIsf", SettingsEvidenceSetting.Isf, "isfObserved", "isfProfile"),
        Setting("settingCarbRatio", SettingsEvidenceSetting.CarbRatio, "carbRatioObserved", "carbRatioProfile"),

        EventCell("glucose", "glucoseP50", Band("minute", "glucoseP50", "glucoseP25", "glucoseP75", "series"), SeriesLine("glucoseP50")) with
        {
            FromStartField = "deltaP50",
            FromStartMarks = [Band("minute", "deltaP50", "deltaP25", "deltaP75", "series"), SeriesLine("deltaP50")],
        },
        EventCell("glucoseWide", null, Edges("minute", "glucoseP10", "glucoseP90", "percentiles", "series")) with
        {
            FromStartMarks = [Edges("minute", "deltaP10", "deltaP90", "percentiles", "series")],
        },
        EventCell("iob", "iobP50"),
        EventCell("cob", "cobP50"),
        EventCell("netLoop", "netLoopCorrectionP50"),

        Day("timeInRange", "timeInRange", true),
        Day("timeBelow", "timeBelow", true),
        Day("timeAbove", "timeAbove", true),
        new(Daily, "ranges", DailySummaryParameters.Name, null, null, [Stacked("day", ["timeBelow", "timeInRange", "timeAbove"])], [], false),
        Day("mean", "mean", false),
        Day("readings", "readings", true) with { Smooth = [], Trend = false },
        Day("totalInsulin", "totalInsulin", true),
        Day("totalBolus", "totalBolus", true),
        Day("totalBasal", "totalBasal", true),
        Day("carbs", "totalCarbs", true),
        new(Daily, "sleep", SleepParameters.Name, null, "totalSleep",
            [Bars("night", "totalSleep"), Stacked("night", ["deepSleep", "lightSleep", "remSleep", "awake"])], [], false),

        AgainstCell("glucose", GlucoseSeriesParameters.Name, "glucose"),
        AgainstCell("iob", GlucoseSeriesParameters.Name, "iob"),
        AgainstCell("cob", GlucoseSeriesParameters.Name, "cob"),
        AgainstCell("basal", BasalDeliveryParameters.Name, "delivered"),
        AgainstCell("heartRate", HeartRateParameters.Name, "bpm"),

        Lanes("sensorChanges", DeviceEventsParameters.Name, ReportDeviceEventGroup.Sensor),
        Lanes("siteChanges", DeviceEventsParameters.Name, ReportDeviceEventGroup.Site),
        Lanes("pumpEvents", DeviceEventsParameters.Name, ReportDeviceEventGroup.Pump),
        Lanes("deviceAlarms", SystemEventsParameters.Name),
        Lanes("trackers", TrackersParameters.Name),
        Span("temporaryTargets", ReportSpanKind.TemporaryTarget),
        Span("pumpConnection", ReportSpanKind.PumpConnectivity),
        Span("dataExclusions", ReportSpanKind.DataExclusion),
        Span("motion", ReportSpanKind.Motion),
        Lanes("sleepStages", SleepStagesParameters.Name),
        Lanes("alertPeriods", AlertExcursionsParameters.Name),
        Lanes("alertsRaised", AlertInstancesParameters.Name),
        Lanes("doNotDisturb", DndWindowsParameters.Name),
        Lanes("compressionLows", CompressionLowsParameters.Name),
        Lanes("glucoseEpisodes", GlucoseEpisodesParameters.Name),
        Night("sleepScore", "score"),
        Night("sleepEfficiency", "efficiency"),
        Night("sleepLatency", "latency"),
        Night("restlessPeriods", "restless"),
        Night("sleepHeartRate", "avgHeartRate") with
        {
            Marks = [Lines("night", ["avgHeartRate", "minHeartRate"]), Points("night", "avgHeartRate", "minHeartRate")],
        },
        Night("sleepHrv", "hrv"),
        Night("sleepSpo2", "spo2"),
        Night("sleepBreathing", "breathingRate"),
        Measured("heartRateInSleep", SleepBiometricsParameters.Name, "heartRate"),
        Measured("hrvInSleep", SleepBiometricsParameters.Name, "hrv"),
        Measured("spo2InSleep", SleepBiometricsParameters.Name, "spo2"),
        Measured("breathingInSleep", SleepBiometricsParameters.Name, "breathingRate"),
        new(Timeline, "labHbA1c", LabHbA1cParameters.Name, null, "hba1c", [Points("day", "hba1c"), Line("day", "hba1c")], [], false),
        new(Daily, "labHbA1c", LabHbA1cParameters.Name, null, "hba1c", [Points("day", "hba1c"), Line("day", "hba1c")], [], false),
        Measured("weight", BodyWeightParameters.Name, "weight") with { Smooth = [] },
        Measured("bodyFat", BodyWeightParameters.Name, "bodyFat") with { Smooth = [] },
        Measured("leanMass", BodyWeightParameters.Name, "leanMass") with { Smooth = [] },
        Measured("trendRate", SensorDetailParameters.Name, "trendRate") with { Combine = true },
        new(Timeline, "sensorNoise", SensorDetailParameters.Name, null, "noise", [Points("time", "noise"), Bars("time", "noise")], [], false),
        new(Timeline, "meterGlucose", MeterGlucoseParameters.Name, null, "meterGlucose", [Points("time", "meterGlucose")], [], false),
    ];

    /// <summary>A timeline cell of a lane dataset (<see cref="LaneDataset"/>), narrowed to one group of device events where one is named.</summary>
    private static CellSpec Lanes(string signal, string dataset, ReportDeviceEventGroup? group = null) =>
        new(Timeline, signal, dataset,
            group is { } g ? new Dictionary<string, object> { ["groups"] = new[] { ReportWire.Name(g) } } : null,
            null, [Spans], [], false);

    /// <summary>A daily cell of one sleep session's figure, along the night it ended.</summary>
    private static CellSpec Night(string signal, string field) =>
        new(Daily, signal, SleepNightsParameters.Name, null, field, [Points("night", field), Line("night", field), Bars("night", field)], [], true);

    /// <summary>A timeline cell of a measured series drawn as a line or its points.</summary>
    private static CellSpec Measured(string signal, string dataset, string field) =>
        new(Timeline, signal, dataset, null, field, [Line("time", field), Points("time", field)], TimeSmooth, true);

    /// <summary>
    /// A signal as the table writes it. <paramref name="Family"/> is the lane family that draws it, and
    /// <paramref name="Section"/> and <paramref name="Group"/> are where the catalogue lists it.
    /// <paramref name="Search"/> are ids of other words a search finds it by. <paramref name="Sources"/> are the
    /// <see cref="ReportDataSources"/> whose records it draws: it has data in a range when any of them has a record there.
    /// </summary>
    internal sealed record SignalSpec(
        string Id, string Family, string Color, string Section, string Group, string[] Search, string[] Sources, string? Lane = null);

    /// <summary>
    /// The signals; the catalogue lists each group's in this order. A signal's color is that of its first
    /// cell's field (<see cref="LeadColor"/>); the color here is used only where none of its cells has a field with one.
    /// A new signal is one row here and its cells in <see cref="Table"/>; the web adds its words.
    /// </summary>
    internal static readonly IReadOnlyList<SignalSpec> SignalTable =
    [
        new("glucose", "Glucose", "glucose-in-range", "glucose", "sensor", ["bg", "bloodSugar", "sensor", "cgm"], [ReportDataSources.SensorGlucose]),
        new("target", "Glucose", "glucose-tight-range", "glucose", "sensor", ["targetRange", "bg"], [ReportDataSources.SensorGlucose]),
        new("glucoseWide", "Glucose", "glucose-in-range", "glucose", "sensor", ["percentile", "bg", "cgm"], [ReportDataSources.SensorGlucose]),
        new("mean", "Glucose", "glucose-in-range", "glucose", "statistics", ["average", "bg"], [ReportDataSources.SensorGlucose]),
        new("timeInRange", "Glucose", "glucose-tight-range", "glucose", "ranges", ["tir", "range"], [ReportDataSources.SensorGlucose]),
        new("timeBelow", "Glucose", "glucose-low", "glucose", "ranges", ["tbr", "low", "range"], [ReportDataSources.SensorGlucose]),
        new("timeAbove", "Glucose", "glucose-high", "glucose", "ranges", ["tar", "high", "range"], [ReportDataSources.SensorGlucose]),
        new("ranges", "Glucose", "glucose-in-range", "glucose", "ranges", ["tir", "range"], [ReportDataSources.SensorGlucose]),
        new("readings", "Glucose", "system-event-info", "dataQuality", "coverage", ["sensor", "cgm", "gaps"], [ReportDataSources.SensorGlucose]),
        new("iob", "Insulin", "insulin", "insulin", "onBoard", ["iob", "insulin"],
            [ReportDataSources.ApsSnapshots, ReportDataSources.Boluses, ReportDataSources.TempBasals]),
        new("basal", "Insulin", "basal", "insulin", "basal", ["rate", "tempBasal", "insulin"],
            [ReportDataSources.TempBasals, ReportDataSources.ProfileActivations]),
        new("basalScheduled", "Insulin", "insulin-scheduled-basal", "insulin", "basal", ["rate", "profile", "insulin"],
            [ReportDataSources.TempBasals, ReportDataSources.ProfileActivations]),
        new("boluses", "Insulin", "insulin-bolus", "insulin", "bolus", ["bolus", "dose", "insulin"], [ReportDataSources.Boluses]),
        new("injections", "Insulin", "insulin-basal", "insulin", "basal", ["mdi", "pen", "dose", "insulin"], [ReportDataSources.BasalInjections]),
        new("netLoop", "Insulin", "insulin-additional-basal", "loop", "corrections", ["loop", "automation", "insulin"],
            [ReportDataSources.TempBasals, ReportDataSources.ApsSnapshots]),
        new("totalInsulin", "Insulin", "insulin", "insulin", "totals", ["tdd", "dose", "insulin"],
            [ReportDataSources.Boluses, ReportDataSources.TempBasals, ReportDataSources.BasalInjections]),
        new("totalBolus", "Insulin", "insulin-bolus", "insulin", "bolus", ["bolus", "dose", "insulin"], [ReportDataSources.Boluses]),
        new("totalBasal", "Insulin", "basal", "insulin", "basal", ["tdd", "insulin"],
            [ReportDataSources.TempBasals, ReportDataSources.ProfileActivations, ReportDataSources.BasalInjections]),
        new("cob", "Food", "carbs-on-board", "food", "carbs", ["cob", "meal", "food"], [ReportDataSources.ApsSnapshots, ReportDataSources.Carbs]),
        new("carbs", "Food", "carbs", "food", "carbs", ["meal", "food"], [ReportDataSources.Carbs]),
        new("reservoir", "Devices", "insulin-basal", "pumpDevices", "pump", ["pump", "pod", "insulin"], [ReportDataSources.PumpSnapshots]),
        new("pumpBattery", "Devices", "pump-mode-boost", "pumpDevices", "pump", ["pump", "pod", "battery"], [ReportDataSources.PumpSnapshots]),
        new("uploaderBattery", "Devices", "pump-mode-sleep", "pumpDevices", "phone", ["phone", "battery"], [ReportDataSources.UploaderSnapshots]),
        new("settingBasal", "Settings", "basal", "settings", "againstData", ["profile", "rate"], [ReportDataSources.SensorGlucose]),
        new("settingIsf", "Settings", "insulin", "settings", "againstData", ["isf", "sensitivity", "profile"], [ReportDataSources.SensorGlucose]),
        new("settingCarbRatio", "Settings", "carbs", "settings", "againstData", ["icr", "profile"], [ReportDataSources.SensorGlucose]),
        new("heartRate", "Body", "heart-rate", "activityBody", "heart", ["hr", "pulse", "exercise"], [ReportDataSources.HeartRates]),
        new("steps", "Body", "steps", "activityBody", "movement", ["walking", "exercise"], [ReportDataSources.StepCounts]),
        new("sleep", "Body", "pump-mode-sleep", "sleep", "sessions", ["night", "bed"], [ReportDataSources.SleepSessions], Lane: "Context"),
        new("pumpModes", "Context", "pump-mode-automatic", "loop", "modes", ["loop", "automation", "pump"], [ReportDataSources.PumpModeSpans]),
        new("profiles", "Context", "pump-mode-liberty", "settings", "profiles", ["profile"], [ReportDataSources.ProfileActivations]),
        new("overrides", "Context", "pump-mode-boost", "context", "marked", ["override", "targetRange", "loop"], [ReportDataSources.OverrideSpans]),
        new("activities", "Context", "pump-mode-exercise", "context", "marked", ["exercise", "illness"], [ReportDataSources.ActivitySpans]),
        new("tempBasals", "Context", "insulin-temp-basal", "loop", "corrections", ["tempBasal", "loop", "rate"], [ReportDataSources.TempBasals]),
        new("bgChecks", "Context", "glucose-in-range", "glucose", "meter", ["bg", "meter", "fingerstick"], [ReportDataSources.BgChecks]),
        new("meterGlucose", "Glucose", "glucose-in-range", "glucose", "meter", ["bg", "meter", "fingerstick", "pump"], [ReportDataSources.MeterGlucose]),
        new("trendRate", "Glucose", "chart-4", "glucose", "sensor", ["roc", "trend", "cgm"], [ReportDataSources.SensorTrendRates]),
        new("glucoseEpisodes", "Context", "glucose-low", "glucose", "ranges", ["low", "high", "episode"], [ReportDataSources.SensorGlucose]),
        new("labHbA1c", "Body", "glucose-in-range", "glucose", "lab", ["a1c", "lab"], [ReportDataSources.LabHbA1c]),
        new("sensorChanges", "Devices", "tracker-sensor", "pumpDevices", "sensor", ["sensor", "cgm", "sage"], [ReportDataSources.SensorEvents], Lane: "Context"),
        new("siteChanges", "Devices", "tracker-cannula", "pumpDevices", "pump", ["site", "cannula", "pod", "cage", "insulin"],
            [ReportDataSources.SiteEvents], Lane: "Context"),
        new("pumpEvents", "Devices", "pump-mode-suspended", "pumpDevices", "pump", ["pump", "suspend", "battery"], [ReportDataSources.PumpEvents], Lane: "Context"),
        new("pumpConnection", "Context", "pump-mode-automatic", "pumpDevices", "pump", ["pump", "bluetooth"], [ReportDataSources.PumpConnectivitySpans]),
        new("trackers", "Devices", "tracker-consumable", "pumpDevices", "consumables", ["sage", "cage", "consumable", "site", "sensor"],
            [ReportDataSources.Trackers], Lane: "Context"),
        new("sleepStages", "Body", "activity-sleep", "sleep", "sessions", ["hypnogram", "rem", "night"], [ReportDataSources.SleepStages], Lane: "Context"),
        new("sleepScore", "Body", "activity-sleep", "sleep", "nightly", ["night", "score"], [ReportDataSources.SleepSessions]),
        new("sleepEfficiency", "Body", "chart-4", "sleep", "nightly", ["night"], [ReportDataSources.SleepSessions]),
        new("sleepLatency", "Body", "chart-5", "sleep", "nightly", ["night", "bed"], [ReportDataSources.SleepSessions]),
        new("restlessPeriods", "Body", "chart-3", "sleep", "nightly", ["night"], [ReportDataSources.SleepSessions]),
        new("sleepHeartRate", "Body", "heart-rate", "sleep", "nightly", ["hr", "pulse", "night"], [ReportDataSources.SleepSessions]),
        new("sleepHrv", "Body", "chart-4", "sleep", "nightly", ["hrv", "night"], [ReportDataSources.SleepSessions]),
        new("sleepSpo2", "Body", "chart-3", "sleep", "nightly", ["spo2", "night"], [ReportDataSources.SleepSessions]),
        new("sleepBreathing", "Body", "chart-5", "sleep", "nightly", ["breathing", "night"], [ReportDataSources.SleepSessions]),
        new("heartRateInSleep", "Body", "heart-rate", "sleep", "body", ["hr", "pulse", "night"], [ReportDataSources.SleepBiometrics]),
        new("hrvInSleep", "Body", "chart-4", "sleep", "body", ["hrv", "night"], [ReportDataSources.SleepBiometrics]),
        new("spo2InSleep", "Body", "chart-3", "sleep", "body", ["spo2", "night"], [ReportDataSources.SleepBiometrics]),
        new("breathingInSleep", "Body", "chart-5", "sleep", "body", ["breathing", "night"], [ReportDataSources.SleepBiometrics]),
        new("motion", "Context", "steps", "activityBody", "movement", ["walking", "exercise"], [ReportDataSources.MotionSpans]),
        new("weight", "Body", "chart-4", "activityBody", "weight", ["weight"], [ReportDataSources.BodyWeights]),
        new("bodyFat", "Body", "chart-5", "activityBody", "weight", ["weight"], [ReportDataSources.BodyFat]),
        new("leanMass", "Body", "chart-3", "activityBody", "weight", ["weight"], [ReportDataSources.LeanMass]),
        new("temporaryTargets", "Context", "chart-3", "context", "marked", ["targetRange", "loop"], [ReportDataSources.TemporaryTargetSpans]),
        new("alertPeriods", "Context", "system-event-alarm", "alerts", "alertRules", ["alert", "alarm"], [ReportDataSources.AlertExcursions]),
        new("alertsRaised", "Context", "system-event-warning", "alerts", "alertRules", ["alert", "alarm", "snooze"], [ReportDataSources.AlertInstances]),
        new("doNotDisturb", "Context", "pump-mode-sleep", "alerts", "quiet", ["dnd", "alert"], [ReportDataSources.DndWindows]),
        new("deviceAlarms", "Devices", "system-event-alarm", "alerts", "deviceAlarms", ["alarm", "pump", "cgm"], [ReportDataSources.SystemEvents], Lane: "Context"),
        new("dataExclusions", "Context", "muted-foreground", "dataQuality", "coverage", ["gaps", "compression"], [ReportDataSources.DataExclusionSpans]),
        new("compressionLows", "Context", "glucose-low", "dataQuality", "coverage", ["compression", "low", "night"], [ReportDataSources.CompressionLows]),
        new("sensorNoise", "Devices", "muted-foreground", "dataQuality", "coverage", ["noise", "sensor", "cgm"], [ReportDataSources.SensorNoise]),
    ];

    /// <summary>The catalogue's sections and the groups in each, in the order it lists them.</summary>
    private static readonly (string Id, string[] Groups)[] SectionTable =
    [
        ("glucose", ["sensor", "meter", "ranges", "statistics", "lab"]),
        ("insulin", ["onBoard", "bolus", "basal", "totals"]),
        ("food", ["carbs"]),
        ("loop", ["corrections", "modes"]),
        ("pumpDevices", ["pump", "phone", "sensor", "consumables"]),
        ("sleep", ["sessions", "nightly", "body"]),
        ("activityBody", ["heart", "movement", "weight"]),
        ("settings", ["againstData", "profiles"]),
        ("context", ["marked"]),
        ("alerts", ["alertRules", "deviceAlarms", "quiet"]),
        ("dataQuality", ["coverage"]),
    ];

    private static readonly (string Id, double Height)[] FamilyTable =
        [("Glucose", 2), ("Insulin", 0.75), ("Food", 0.75), ("Devices", 0.75), ("Settings", 1), ("Body", 0.75), ("Context", 0.5)];

    private static readonly (EventAnchorKind Anchor, string Group)[] AnchorTable =
    [
        (EventAnchorKind.Meal, "food"), (EventAnchorKind.CorrectionBolus, "food"), (EventAnchorKind.AnyBolus, "food"),
        (EventAnchorKind.UnannouncedRise, "food"),
        (EventAnchorKind.LowStart, "glucose"), (EventAnchorKind.HighStart, "glucose"), (EventAnchorKind.LowTreatment, "glucose"),
        (EventAnchorKind.ExerciseStart, "activity"), (EventAnchorKind.SleepStart, "activity"), (EventAnchorKind.Wake, "activity"),
        (EventAnchorKind.SensorStart, "devices"), (EventAnchorKind.SiteChange, "devices"), (EventAnchorKind.OverrideStart, "devices"),
        (EventAnchorKind.TemporaryTargetStart, "devices"),
    ];

    private static readonly (string Id, string Property)[] LeaveOutTable =
    [
        ("overrides", nameof(LoopAnalysisDatasetParameters.ExcludeOverrides)),
        ("temporaryTargets", nameof(LoopAnalysisDatasetParameters.ExcludeTemporaryTargets)),
        ("exercise", nameof(LoopAnalysisDatasetParameters.ExcludeExercise)),
        ("illness", nameof(LoopAnalysisDatasetParameters.ExcludeIllness)),
        ("openLoop", nameof(LoopAnalysisDatasetParameters.ExcludeOpenLoop)),
    ];

    private static string ParameterOf(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    private static IReadOnlyList<string> LeaveOutScopes(string property) =>
        typeof(LoopAnalysisDatasetParameters).GetProperty(property)!.GetCustomAttribute<ReportNeedsScopeAttribute>() is { } needs
            ? [needs.Scope]
            : [];

    private static readonly (string Id, int Hours)[] RangeTable = [("6h", 6), ("24h", 24), ("7d", 168), ("14d", 336), ("28d", 672), ("90d", 2160)];

    private static readonly (string Id, ReportWeekday[][] Groups)[] DayGroupTable =
    [
        ("everyDay", [Enum.GetValues<ReportWeekday>()]),
        ("weekdaysAndWeekend", [[ReportWeekday.Mon, ReportWeekday.Tue, ReportWeekday.Wed, ReportWeekday.Thu, ReportWeekday.Fri], [ReportWeekday.Sat, ReportWeekday.Sun]]),
        ("fridayAndWeekend", [[ReportWeekday.Mon, ReportWeekday.Tue, ReportWeekday.Wed, ReportWeekday.Thu], [ReportWeekday.Fri], [ReportWeekday.Sat, ReportWeekday.Sun]]),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The vocabulary for a caller holding <paramref name="granted"/>, resolved against <paramref name="registry"/>.</summary>
    public static CustomReportWorkspace Build(IReportDatasetRegistry registry, IReadOnlySet<string> granted)
    {
        bool Holds(IEnumerable<string> scopes) => scopes.All(s => Scope.Satisfies(granted, s));
        var cells = Table.Select(c => Resolve(c, registry, Holds)).ToList();
        var outcomesDataset = registry.Find(EventOutcomesParameters.Name)!;
        var occurrence = registry.Find(EventOccurrenceParameters.Name)!;

        return new CustomReportWorkspace(
            [Timeline, TypicalDay, Event, Daily, Against],
            [.. FamilyTable.Select(f => new CustomReportWorkspaceFamily(f.Id, f.Height))],
            [.. SectionTable.Select(s => new CustomReportWorkspaceSection(s.Id, s.Groups))],
            [.. SignalTable.Select(s =>
            {
                var own = cells.Where(c => c.Signal == s.Id).ToList();
                var lenses = new[] { Timeline, TypicalDay, Event, Daily, Against }.Where(l => own.Any(c => c.Lens == l)).ToList();
                return new CustomReportWorkspaceSignal(s.Id, s.Family, s.Lane,
                    Table.Where(c => c.Signal == s.Id).Select(c => LeadColor(c, registry)).OfType<ChartColor>().Select(ReportWire.Name).FirstOrDefault(s.Color),
                    own.Any(c => c.Permitted),
                    s.Section, s.Group,
                    own.Select(c => c.Unit).FirstOrDefault(u => u is not null),
                    lenses,
                    own.Count == 0 ? 0 : own.Max(c => c.MaxRangeHours),
                    s.Search);
            })],
            cells,
            [.. AnchorTable.Select(a => a.Group).Distinct()],
            [.. AnchorTable.Select(a =>
            {
                var scopes = LoopAnalysisReadScopeGuard.ScopesOf(a.Anchor);
                return new CustomReportWorkspaceAnchor(a.Anchor.ToString(), a.Group, a.Anchor == EventAnchorKind.Meal,
                    EventAnchors.ReadsCorroboration(a.Anchor), scopes, Holds(scopes));
            })],
            [.. Enum.GetValues<ReportDayKind>().Select(k =>
            {
                IReadOnlyList<string> scopes = new ReportDayFilter { Kind = k }.NeedsTreatments ? [Scope.TreatmentsRead] : [];
                return new CustomReportWorkspaceDayKind(ReportWire.Name(k), scopes, Holds(scopes));
            })],
            [.. DayGroupTable.Select(p => new CustomReportWorkspaceDayGroupPreset(p.Id, [.. p.Groups.Select(g => (IReadOnlyList<string>)[.. g.Select(ReportWire.Name)])]))],
            [.. LeaveOutTable.Select(l => new CustomReportWorkspaceLeaveOut(
                l.Id, ParameterOf(l.Property), LeaveOutScopes(l.Property), Holds(LeaveOutScopes(l.Property))))],
            [.. RangeTable.Select(r => new CustomReportWorkspaceRangePreset(r.Id, r.Hours))],
            DefaultReportHours,
            [DifferenceSpec.Name, RatioSpec.Name],
            AgainstGridMinutes,
            TimelineGrids,
            ReportGrid.MaxSlots,
            TimelineRawHours,
            new CustomReportWorkspaceEvents(
                [
                    new("curve", EventResponseParameters.Name), new("each", EventOutcomesParameters.Name),
                    new("spread", EventOutcomeSpreadParameters.Name), new("when", EventOccurrenceParameters.Name),
                ],
                [.. EventOutcomeInfo.All.Select(o => new CustomReportWorkspaceOutcome(o.Name,
                    !outcomesDataset.Fields.Any(f => f.Name == o.Name && f.IsNumber)))],
                [.. EventOutcomesDataset.AxisFields.Select(f => f.Name)],
                [EventOutcomesDataset.WrappingAxis],
                [.. occurrence.Fields.Where(f => !f.Companion && f.AxisKind is not null && !f.Name.StartsWith("events", StringComparison.Ordinal)).Select(f => f.Name)]),
            new CustomReportWorkspaceSources(
                [GlucoseStatisticsParameters.Name, GlucoseDistributionParameters.Name, DeviceStatusParameters.Name],
                [TreatmentsParameters.Name, DailySummaryParameters.Name, EventResponseParameters.Name, GlucoseSeriesParameters.Name,
                 BasalDeliveryParameters.Name, StateSpansParameters.Name, GlucoseDistributionByHourParameters.Name,
                 DailyProfileParameters.Name, SettingsEvidenceParameters.Name, GlucoseStatisticsParameters.Name],
                PeriodComparisonParameters.Name));
    }

    /// <summary>The fields a cell binds, its <see cref="CellSpec.Field"/> first.</summary>
    private static List<string> Named(CellSpec c) =>
        c.Marks.Concat(c.FromStartMarks ?? [])
            .SelectMany(m => (m.Bindings.Y ?? []).Concat(m.Bindings.Edges ?? []))
            .Prepend(c.Field).Append(c.FromStartField).OfType<string>().Distinct().ToList();

    /// <summary>
    /// The color of the cell's own <see cref="CellSpec.Field"/>, where it has one. A cell with no field of its own
    /// (a band between two edges, a stack, spans) draws each part in its own color, so it gives the signal none.
    /// </summary>
    internal static ChartColor? LeadColor(CellSpec c, IReportDatasetRegistry registry) =>
        c.Field is { } field
            ? registry.Find(c.Dataset)!.FieldsFor(ReportPanelKind.Chart).FirstOrDefault(f => f.Name == field)?.Color
            : null;

    /// <summary>
    /// A cell with what the registry says of it: the scopes of its dataset, the fields it draws and the records it
    /// reads (<see cref="CellSpec.Scopes"/>: a kind of state span is withheld without its own), and its lead field's unit.
    /// </summary>
    private static CustomReportWorkspaceCell Resolve(CellSpec c, IReportDatasetRegistry registry, Func<IEnumerable<string>, bool> holds)
    {
        var dataset = registry.Find(c.Dataset) ?? throw new InvalidOperationException($"The workspace names no dataset '{c.Dataset}'.");
        var fields = dataset.FieldsFor(ReportPanelKind.Chart);
        var named = Named(c);
        var scopes = named.Select(n => fields.FirstOrDefault(f => f.Name == n)?.Scope).OfType<string>()
            .Prepend(dataset.RequiredScope).Concat(c.Scopes ?? []).Distinct().ToList();
        var lead = c.Field ?? named.FirstOrDefault();
        var schema = ReportParametersSchema.For(dataset.ParametersType);
        bool Takes(string parameter) => schema.TryGetProperty("properties", out var taken) && taken.TryGetProperty(parameter, out _);
        var unit = lead is null ? null : fields.FirstOrDefault(f => f.Name == lead)?.Unit;
        return new CustomReportWorkspaceCell(
            c.Lens, c.Signal, c.Dataset,
            c.Parameters is null ? null : JsonSerializer.SerializeToElement(c.Parameters, Json),
            c.Field, unit, c.Marks, c.FromStartField, c.FromStartMarks, c.Smooth, c.Trend, c.Combine,
            Takes("dayOfWeek"), Takes("days"), LeaveOutTable.All(l => Takes(ParameterOf(l.Property))), c.SpanKind, scopes, holds(scopes),
            dataset.MaxRange.TotalHours);
    }
}
