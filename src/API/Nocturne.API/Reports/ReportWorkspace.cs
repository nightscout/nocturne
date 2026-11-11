using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Nocturne.API.Authorization;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Reports.Derived;
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
public sealed record CustomReportWorkspace(
    [property: Required] IReadOnlyList<string> Lenses,
    [property: Required] IReadOnlyList<CustomReportWorkspaceFamily> Families,
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
    [property: Required] CustomReportWorkspaceEvents Events,
    [property: Required] CustomReportWorkspaceSources Sources);

/// <summary>A family of signals and the share of a chart's height its lane takes.</summary>
public sealed record CustomReportWorkspaceFamily([property: Required] string Id, [property: Required] double LaneHeight);

/// <summary>A signal: its family, the lane it is drawn in when not its family's, and its theme color token.</summary>
/// <param name="Permitted">Whether the caller holds the scopes of at least one of its cells.</param>
public sealed record CustomReportWorkspaceSignal(
    [property: Required] string Id,
    [property: Required] string Family,
    string? Lane,
    [property: Required] string Color,
    [property: Required] bool Permitted);

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
    [property: Required] bool Permitted);

/// <summary>One way a cell is drawn: the spec's mark, the word id the web offers it by, and its bindings.</summary>
/// <param name="Word">line, lines, area, points, bars, stacked, medianBand, band, range, percentiles, median, markers or spans.</param>
public sealed record CustomReportWorkspaceMark(
    [property: Required] string Mark,
    [property: Required] string Word,
    [property: Required] CustomReportLayerBindings Bindings);

/// <summary>An event anchor as the workspace lists it.</summary>
/// <param name="MealNarrowing">Grams, foods and a food category narrow its groups: meals only, as the validator refuses them elsewhere.</param>
/// <param name="Scopes">The read scopes its events need, as the scope guard requires them.</param>
public sealed record CustomReportWorkspaceAnchor(
    [property: Required] string Id,
    [property: Required] string Group,
    [property: Required] bool MealNarrowing,
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
public sealed record CustomReportWorkspaceLeaveOut([property: Required] string Id, [property: Required] string Parameter);

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
        IReadOnlyList<CustomReportWorkspaceMark>? FromStartMarks = null);

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
        new(Timeline, signal, "glucose_series", null, field, [Line("time", field), Area("time", field)], TimeSmooth, true, Combine: true);

    private static CellSpec Treatments(string signal, string type, string field) =>
        new(Timeline, signal, "treatments", new Dictionary<string, object> { ["types"] = new[] { type } }, field,
            [Markers(field), Bars("time", field)], [], false);

    private static CellSpec Device(string signal, string field) =>
        new(Timeline, signal, "device_status", null, field, [Line("time", field), Points("time", field)], TimeSmooth, true);

    private static CellSpec Span(string signal, string kind) =>
        new(Timeline, signal, "state_spans", new Dictionary<string, object> { ["kinds"] = new[] { kind } }, null, [Spans], [], false, SpanKind: kind);

    private static CellSpec Profile(string signal, string? field, params CustomReportWorkspaceMark[] marks) =>
        new(TypicalDay, signal, "daily_profile", null, field,
            marks.Length > 0 ? marks : [Line("minuteOfDay", field!), Area("minuteOfDay", field!)],
            FoldSmooth, true, Combine: true);

    private static CellSpec Setting(string signal, string setting, string observed, string inProfile) =>
        new(TypicalDay, signal, "settings_evidence", new Dictionary<string, object> { ["setting"] = setting }, observed,
            [Lines("startMinute", [observed, inProfile]), Points("startMinute", observed, inProfile)], [], false);

    private static CellSpec Day(string signal, string field, bool barsFirst) =>
        new(Daily, signal, "daily_summary", null, field,
            barsFirst ? [Bars("day", field), Line("day", field), Points("day", field)] : [Line("day", field), Points("day", field), Bars("day", field)],
            DaySmooth, true, Combine: true);

    private static CellSpec EventCell(string signal, string? field, params CustomReportWorkspaceMark[] marks) =>
        new(Event, signal, EventResponseParameters.Name, null, field, marks.Length > 0 ? marks : [SeriesLine(field!)], [], false);

    private static CellSpec AgainstCell(string signal, string dataset, string field) => new(Against, signal, dataset, null, field, [], [], false);

    /// <summary>The signal x lens table, as the web's workspace drew it. A signal missing from a lens is not drawn under it.</summary>
    internal static readonly IReadOnlyList<CellSpec> Table =
    [
        Series("glucose", "glucose") with { Marks = [Line("time", "glucose"), Points("time", "glucose")] },
        new(Timeline, "target", "glucose_series", null, null, [Edges("time", "targetLow", "targetHigh", "band")], [], false),
        Series("iob", "iob"),
        Series("cob", "cob"),
        new(Timeline, "basal", "basal_delivery", null, "delivered", [Area("time", "delivered"), Line("time", "delivered")], TimeSmooth, true, Combine: true),
        new(Timeline, "basalScheduled", "basal_delivery", null, "scheduled", [Line("time", "scheduled"), Area("time", "scheduled")], [], false),
        Treatments("boluses", "bolus", "insulin"),
        Treatments("injections", "basal_injection", "injectionUnits"),
        Treatments("carbs", "carbs", "carbs"),
        Treatments("bgChecks", "bg_check", "bgCheck") with { Marks = [Markers("bgCheck"), Points("time", "bgCheck")] },
        Device("reservoir", "reservoir"),
        Device("pumpBattery", "pumpBattery"),
        Device("uploaderBattery", "uploaderBattery"),
        new(Timeline, "heartRate", "heart_rate", null, "bpm",
            [Line("time", "bpm"), new("band", "range", Bind("time", ["bpm"], ["bpmMin", "bpmMax"]))], TimeSmooth, true, Combine: true),
        new(Timeline, "steps", "steps", null, "steps", [Bars("time", "steps"), Line("time", "steps")], [], false),
        new(Timeline, "sleep", "sleep", null, null, [Spans], [], false),
        Span("pumpModes", "pump_mode"),
        Span("profiles", "profile"),
        Span("overrides", "override"),
        Span("activities", "activity"),
        Span("tempBasals", "temp_basal"),

        Profile("glucose", "glucoseP50",
            Band("minuteOfDay", "glucoseP50", "glucoseP25", "glucoseP75"), Line("minuteOfDay", "glucoseP50", "median")),
        Profile("target", null, Edges("minuteOfDay", "targetLow", "targetHigh", "band")) with { Smooth = [], Trend = false, Combine = false },
        Profile("glucoseWide", null, Edges("minuteOfDay", "glucoseP10", "glucoseP90", "percentiles")) with { Smooth = [], Trend = false, Combine = false },
        Profile("readings", "readings") with { Smooth = [] },
        new(TypicalDay, "ranges", "glucose_distribution_by_hour", null, null,
            [Stacked("hour", ["veryLow", "low", "tightRange", "aboveTightRange", "high", "veryHigh"])], [], false),
        new(TypicalDay, "mean", "glucose_distribution_by_hour", null, "mean", [Line("hour", "mean"), Bars("hour", "mean")], [], true),
        Profile("iob", "iob"),
        Profile("cob", "cob"),
        Profile("basal", "deliveredBasal"),
        Profile("basalScheduled", "scheduledBasal") with { Smooth = [], Trend = false, Combine = false },
        Profile("netLoop", "netLoopCorrection", Area("minuteOfDay", "netLoopCorrection"), Line("minuteOfDay", "netLoopCorrection"))
            with { Smooth = [], Trend = false, Combine = false },
        Setting("settingBasal", "basal", "basalObserved", "basalProfile"),
        Setting("settingIsf", "isf", "isfObserved", "isfProfile"),
        Setting("settingCarbRatio", "carb_ratio", "carbRatioObserved", "carbRatioProfile"),

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
        new(Daily, "ranges", "daily_summary", null, null, [Stacked("day", ["timeBelow", "timeInRange", "timeAbove"])], [], false),
        Day("mean", "mean", false),
        Day("readings", "readings", true) with { Smooth = [], Trend = false },
        Day("totalInsulin", "totalInsulin", true),
        Day("totalBolus", "totalBolus", true),
        Day("totalBasal", "totalBasal", true),
        Day("carbs", "totalCarbs", true),
        new(Daily, "sleep", "sleep", null, "totalSleep",
            [Bars("night", "totalSleep"), Stacked("night", ["deepSleep", "lightSleep", "remSleep", "awake"])], [], false),

        AgainstCell("glucose", "glucose_series", "glucose"),
        AgainstCell("iob", "glucose_series", "iob"),
        AgainstCell("cob", "glucose_series", "cob"),
        AgainstCell("basal", "basal_delivery", "delivered"),
        AgainstCell("heartRate", "heart_rate", "bpm"),
    ];

    private static readonly (string Id, string Family, string Color, string? Lane)[] SignalTable =
    [
        ("glucose", "Glucose", "glucose-in-range", null),
        ("target", "Glucose", "glucose-tight-range", null),
        ("glucoseWide", "Glucose", "glucose-in-range", null),
        ("mean", "Glucose", "glucose-in-range", null),
        ("timeInRange", "Glucose", "glucose-tight-range", null),
        ("timeBelow", "Glucose", "glucose-low", null),
        ("timeAbove", "Glucose", "glucose-high", null),
        ("ranges", "Glucose", "glucose-in-range", null),
        ("readings", "Glucose", "system-event-info", null),
        ("iob", "Insulin", "insulin", null),
        ("basal", "Insulin", "basal", null),
        ("basalScheduled", "Insulin", "insulin-scheduled-basal", null),
        ("boluses", "Insulin", "insulin-bolus", null),
        ("injections", "Insulin", "insulin-basal", null),
        ("netLoop", "Insulin", "insulin-additional-basal", null),
        ("totalInsulin", "Insulin", "insulin", null),
        ("totalBolus", "Insulin", "insulin-bolus", null),
        ("totalBasal", "Insulin", "basal", null),
        ("cob", "Food", "carbs-on-board", null),
        ("carbs", "Food", "carbs", null),
        ("reservoir", "Devices", "insulin-basal", null),
        ("pumpBattery", "Devices", "pump-mode-boost", null),
        ("uploaderBattery", "Devices", "pump-mode-sleep", null),
        ("settingBasal", "Settings", "basal", null),
        ("settingIsf", "Settings", "insulin", null),
        ("settingCarbRatio", "Settings", "carbs", null),
        ("heartRate", "Body", "heart-rate", null),
        ("steps", "Body", "steps", null),
        ("sleep", "Body", "pump-mode-sleep", "Context"),
        ("pumpModes", "Context", "pump-mode-automatic", null),
        ("profiles", "Context", "pump-mode-liberty", null),
        ("overrides", "Context", "pump-mode-boost", null),
        ("activities", "Context", "pump-mode-exercise", null),
        ("tempBasals", "Context", "insulin-temp-basal", null),
        ("bgChecks", "Context", "glucose-in-range", null),
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

    private static readonly (string Id, string Parameter)[] LeaveOutTable =
    [
        ("overrides", "excludeOverrides"), ("temporaryTargets", "excludeTemporaryTargets"), ("exercise", "excludeExercise"),
        ("illness", "excludeIllness"), ("openLoop", "excludeOpenLoop"),
    ];

    private static readonly (string Id, int Hours)[] RangeTable = [("6h", 6), ("24h", 24), ("7d", 168), ("14d", 336), ("28d", 672), ("90d", 2160)];

    private static readonly (string Id, string[][] Groups)[] DayGroupTable =
    [
        ("everyDay", [["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]]),
        ("weekdaysAndWeekend", [["Mon", "Tue", "Wed", "Thu", "Fri"], ["Sat", "Sun"]]),
        ("fridayAndWeekend", [["Mon", "Tue", "Wed", "Thu"], ["Fri"], ["Sat", "Sun"]]),
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
            [.. SignalTable.Select(s => new CustomReportWorkspaceSignal(s.Id, s.Family, s.Lane, s.Color,
                cells.Any(c => c.Signal == s.Id && c.Permitted)))],
            cells,
            [.. AnchorTable.Select(a => a.Group).Distinct()],
            [.. AnchorTable.Select(a =>
            {
                var scopes = LoopAnalysisReadScopeGuard.ScopesOf(a.Anchor);
                return new CustomReportWorkspaceAnchor(a.Anchor.ToString(), a.Group, a.Anchor == EventAnchorKind.Meal, scopes, Holds(scopes));
            })],
            [.. Enum.GetValues<ReportDayKind>().Select(k =>
            {
                IReadOnlyList<string> scopes = new ReportDayFilter { Kind = k }.NeedsTreatments ? [Scope.TreatmentsRead] : [];
                return new CustomReportWorkspaceDayKind(ReportWire.Name(k), scopes, Holds(scopes));
            })],
            [.. DayGroupTable.Select(p => new CustomReportWorkspaceDayGroupPreset(p.Id, [.. p.Groups.Select(g => (IReadOnlyList<string>)g)]))],
            [.. LeaveOutTable.Select(l => new CustomReportWorkspaceLeaveOut(l.Id, l.Parameter))],
            [.. RangeTable.Select(r => new CustomReportWorkspaceRangePreset(r.Id, r.Hours))],
            DefaultReportHours,
            [DifferenceSpec.Name, RatioSpec.Name],
            AgainstGridMinutes,
            TimelineGrids,
            ReportGrid.MaxSlots,
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
                ["glucose_statistics", "glucose_distribution", "device_status"],
                ["treatments", "daily_summary", EventResponseParameters.Name, "glucose_series", "basal_delivery", "state_spans",
                 "glucose_distribution_by_hour", "daily_profile", "settings_evidence", "glucose_statistics"],
                PeriodComparisonParameters.Name));
    }

    /// <summary>A cell with what the registry says of it: the scopes of its dataset and the fields it draws, and its lead field's unit.</summary>
    private static CustomReportWorkspaceCell Resolve(CellSpec c, IReportDatasetRegistry registry, Func<IEnumerable<string>, bool> holds)
    {
        var dataset = registry.Find(c.Dataset) ?? throw new InvalidOperationException($"The workspace names no dataset '{c.Dataset}'.");
        var fields = dataset.FieldsFor(ReportPanelKind.Chart);
        var named = c.Marks.Concat(c.FromStartMarks ?? [])
            .SelectMany(m => (m.Bindings.Y ?? []).Concat(m.Bindings.Edges ?? []))
            .Prepend(c.Field).Append(c.FromStartField).OfType<string>().Distinct().ToList();
        var scopes = named.Select(n => fields.FirstOrDefault(f => f.Name == n)?.Scope).OfType<string>()
            .Prepend(dataset.RequiredScope).Distinct().ToList();
        var lead = c.Field ?? named.FirstOrDefault();
        var schema = ReportParametersSchema.For(dataset.ParametersType);
        bool Takes(string parameter) => schema.TryGetProperty("properties", out var taken) && taken.TryGetProperty(parameter, out _);
        var unit = lead is null ? null : fields.FirstOrDefault(f => f.Name == lead)?.Unit;
        return new CustomReportWorkspaceCell(
            c.Lens, c.Signal, c.Dataset,
            c.Parameters is null ? null : JsonSerializer.SerializeToElement(c.Parameters, Json),
            c.Field, unit, c.Marks, c.FromStartField, c.FromStartMarks, c.Smooth, c.Trend, c.Combine,
            Takes("dayOfWeek"), Takes("days"), LeaveOutTable.All(l => Takes(l.Parameter)), c.SpanKind, scopes, holds(scopes));
    }
}
