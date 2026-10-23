using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>What an event-aligned response curve is aligned on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventAnchorKind>))]
public enum EventAnchorKind
{
    /// <summary>A carb entry; entries at the same instant are one meal.</summary>
    Meal,

    /// <summary>A manual bolus with no carb entry within 30 minutes either side.</summary>
    CorrectionBolus,

    /// <summary>A CGM sensor start or change.</summary>
    SensorStart,

    ExerciseStart,
    OverrideStart,
    TemporaryTargetStart,

    /// <summary>
    /// A meal-like glucose rise with no carb entry near it, for a loop run without announcing meals;
    /// see <c>UnannouncedRises</c>.
    /// </summary>
    UnannouncedRise,
}

/// <summary>Which way glucose is expected to go after an anchor, and so which figures describe it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventResponseKind>))]
public enum EventResponseKind
{
    /// <summary>Glucose is expected to rise: the peak, its time, and the return to the start.</summary>
    Rise,

    /// <summary>Glucose is expected to fall: the drop, the nadir, and whether it went below range.</summary>
    Fall,
}

/// <summary>One cohort of events: an anchor kind, narrowed by time of day and, for meals, by size.</summary>
public class EventCohortDefinition
{
    /// <summary>The cohort's number, 1 to 3, as its query parameters are suffixed.</summary>
    public int Cohort { get; set; }

    public EventAnchorKind Anchor { get; set; }

    /// <summary>
    /// Start of the local time-of-day window the anchor must fall in, in minutes after midnight;
    /// with <see cref="ToMinute"/> before it, the window wraps past midnight. Null for all day.
    /// </summary>
    public int? FromMinute { get; set; }

    /// <summary>End of the window, exclusive.</summary>
    public int? ToMinute { get; set; }

    /// <summary>Smallest meal, g, inclusive; meals only.</summary>
    public double? MinGrams { get; set; }

    /// <summary>Largest meal, g, inclusive; meals only.</summary>
    public double? MaxGrams { get; set; }
}

/// <summary>The cohorts and window an event response was computed for.</summary>
public class EventResponseDefinition
{
    public const int SlotMinutes = 5;
    public const int DefaultMinutesBefore = 60;
    public const int DefaultMinutesAfter = 300;
    public const int MaxCohorts = 3;

    /// <summary>A bound no meal reaches, so a typing slip is ignored rather than read.</summary>
    public const double MaxGrams = 1000;

    /// <summary>The windows before the anchor a request may ask for, in minutes.</summary>
    public static readonly IReadOnlyList<int> MinutesBeforeChoices = [0, 30, 60, 90, 120];

    /// <summary>The windows after the anchor a request may ask for, in minutes.</summary>
    public static readonly IReadOnlyList<int> MinutesAfterChoices = [120, 180, 240, 300, 360, 420, 480];

    public List<EventCohortDefinition> Cohorts { get; set; } = [];

    /// <summary>Minutes of the window before the anchor, a multiple of <see cref="SlotMinutes"/>.</summary>
    public int MinutesBefore { get; set; } = DefaultMinutesBefore;

    /// <summary>Minutes of the window after the anchor, a multiple of <see cref="SlotMinutes"/>.</summary>
    public int MinutesAfter { get; set; } = DefaultMinutesAfter;

    /// <summary>
    /// Drop an event when another meal, correction bolus or unannounced rise falls inside its
    /// window, so its curve is its own.
    /// </summary>
    public bool IsolatedOnly { get; set; }

    /// <summary>
    /// Count an unannounced rise only where the loop answered it with a correction of its own; see
    /// <c>UnannouncedRises.CorroborationBasalHours</c>.
    /// </summary>
    public bool RequireCorroboration { get; set; }
}

/// <summary>The bounds of an <see cref="EventResponseDefinition"/>, as the builder offers them.</summary>
public class EventResponseLimits
{
    public int MaxCohorts { get; set; } = EventResponseDefinition.MaxCohorts;

    public double MaxGrams { get; set; } = EventResponseDefinition.MaxGrams;

    public List<int> MinutesBeforeChoices { get; set; } = [.. EventResponseDefinition.MinutesBeforeChoices];

    public List<int> MinutesAfterChoices { get; set; } = [.. EventResponseDefinition.MinutesAfterChoices];
}

/// <summary>An anchor kind the caller's scopes do not let it ask for, and the scope it would need.</summary>
public class EventUnavailableAnchor
{
    public EventAnchorKind Anchor { get; set; }

    public string RequiredScope { get; set; } = "";
}

/// <summary>The Meals and events page: each cohort's response curves, for the primary range and a comparison.</summary>
public class EventResponseReport : LoopAnalysisResponse
{
    /// <summary>The cohorts and window as applied, after the defaults and the caller's scopes.</summary>
    public EventResponseDefinition Definition { get; set; } = new();

    /// <summary>Anchor kinds the caller cannot ask for; a cohort that asks for one is dropped.</summary>
    public List<EventUnavailableAnchor> UnavailableAnchors { get; set; } = [];

    /// <summary>What a request may ask for; a window or grams outside these is ignored.</summary>
    public EventResponseLimits Limits { get; set; } = new();

    /// <summary>
    /// Minutes from the anchor to the start of each slot of the window, earliest first; every
    /// cohort's <see cref="EventCohortResult.Slots"/> follows this order.
    /// </summary>
    public List<int> SlotMinutes { get; set; } = [];

    /// <summary>
    /// mg/dL: how near the start glucose counts as back to start, for
    /// <see cref="EventMetrics.MinutesBackToStart"/>.
    /// </summary>
    public double BackToStartMgdl { get; set; }

    public EventResponseRange Primary { get; set; } = new();

    public EventResponseRange? Comparison { get; set; }
}

public class EventResponseRange
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>One result per cohort of <see cref="EventResponseReport.Definition"/>, in its order.</summary>
    public List<EventCohortResult> Cohorts { get; set; } = [];

    /// <summary>
    /// Every event counted in some cohort, oldest first; each appears once. Listed for the primary
    /// range only.
    /// </summary>
    public List<EventResponseEvent> Events { get; set; } = [];
}

public class EventCohortResult
{
    public int Cohort { get; set; }

    /// <summary>The expected response of the cohort's anchor kind, which decides the figures it carries.</summary>
    public EventResponseKind Response { get; set; }

    /// <summary>Anchors of the cohort's kind and filters in the range's analysed time.</summary>
    public int Anchors { get; set; }

    /// <summary>Anchors dropped, with isolated events only asked for, for another event in the window.</summary>
    public int NotIsolated { get; set; }

    /// <summary>
    /// Anchors dropped for want of a start glucose: a reading within 5 minutes of the anchor, or for
    /// a sensor start the first reading after its warm-up.
    /// </summary>
    public int MissingAnchorGlucose { get; set; }

    /// <summary>
    /// Anchors dropped because what decides them reaches back before the history the caller may
    /// read: a history-clamped caller sees no carbs or boluses there, so a rise could not be told
    /// from a meal nor an event shown isolated.
    /// </summary>
    public int BeforeVisibleHistory { get; set; }

    /// <summary>Anchors counted: the events behind every slot and metric.</summary>
    public int Events { get; set; }

    /// <summary>
    /// Meals given no grams per unit because another meal, correction or rise fell inside the
    /// window, so its insulin is not the meal's alone.
    /// </summary>
    public int GramsPerUnitNotIsolated { get; set; }

    /// <summary>Isolated meals given no grams per unit because no insulin above schedule was delivered in the window.</summary>
    public int GramsPerUnitNoNetInsulin { get; set; }

    /// <summary>
    /// Isolated meals given no grams per unit because the pump's records cover too little of the
    /// window's basal to total its insulin.
    /// </summary>
    public int GramsPerUnitUncovered { get; set; }

    /// <summary>
    /// Events whose window held a reading below range (<see cref="EventMetrics.WentBelowRange"/>);
    /// 0 for a <see cref="EventResponseKind.Rise"/> cohort.
    /// </summary>
    public int WentBelowRange { get; set; }

    /// <summary>
    /// Events with a reading after the anchor, so whether they went below range is known: the
    /// denominator of <see cref="WentBelowRange"/>.
    /// </summary>
    public int WentBelowRangeObserved { get; set; }

    /// <summary>
    /// The window in <see cref="EventResponseDefinition.SlotMinutes"/>-minute slots from the
    /// anchor's exact time, in the order of <see cref="EventResponseReport.SlotMinutes"/>.
    /// </summary>
    public List<EventResponseSlot> Slots { get; set; } = [];

    public EventMetricsSummary Metrics { get; set; } = new();
}

/// <summary>
/// One relative slot of a cohort, each series spread across the cohort's events: n is events.
/// </summary>
public class EventResponseSlot
{
    /// <summary>mg/dL, the slot's mean reading minus the anchor glucose.</summary>
    public LoopAnalysisAggregate? GlucoseChange { get; set; }

    /// <summary>mg/dL.</summary>
    public LoopAnalysisAggregate? Glucose { get; set; }

    /// <summary>U/h, the algorithm's temp delta plus its automatic boluses over the slot.</summary>
    public LoopAnalysisAggregate? NetLoopCorrection { get; set; }

    /// <summary>U, net insulin above schedule from the anchor to the slot's end; absent before the anchor.</summary>
    public LoopAnalysisAggregate? NetInsulinSinceAnchor { get; set; }

    /// <summary>U, as <see cref="NetInsulinSinceAnchor"/> for the algorithm's components alone.</summary>
    public LoopAnalysisAggregate? NetLoopCorrectionSinceAnchor { get; set; }

    /// <summary>U.</summary>
    public LoopAnalysisAggregate? Iob { get; set; }

    /// <summary>g.</summary>
    public LoopAnalysisAggregate? Cob { get; set; }
}

/// <summary>One event, for the event list.</summary>
public class EventResponseEvent
{
    public DateTime At { get; set; }

    /// <summary>The local date, yyyy-MM-dd, as Day in Review's <c>date</c> parameter reads it.</summary>
    public string Date { get; set; } = "";

    /// <summary>The local time, HH:mm, as Day in Review's <c>time</c> parameter reads it.</summary>
    public string Time { get; set; } = "";

    public EventAnchorKind Anchor { get; set; }

    /// <summary>The expected response of <see cref="Anchor"/>, which decides the figures the event carries.</summary>
    public EventResponseKind Response { get; set; }

    /// <summary>g, for a meal.</summary>
    public double? Grams { get; set; }

    /// <summary>The cohorts that counted it.</summary>
    public List<int> Cohorts { get; set; } = [];

    /// <summary>
    /// Which slots of the window held a glucose reading, as base64 of a bitmap: slot <c>i</c> is
    /// bit <c>i % 8</c> (least significant first) of byte <c>i / 8</c>, in the order of
    /// <see cref="EventResponseReport.SlotMinutes"/>. Null when glucose is withheld.
    /// </summary>
    public string? GlucoseCoverage { get; set; }

    public EventMetrics Metrics { get; set; } = new();
}

/// <summary>
/// What one event did. Each figure is null where the data to observe it is missing, or where it
/// belongs to the other <see cref="EventResponseKind"/>; glucose figures are mg/dL, insulin figures U.
/// </summary>
public class EventMetrics
{
    /// <summary>The reading nearest the anchor, within 5 minutes.</summary>
    public double? StartGlucose { get; set; }

    /// <summary>
    /// The highest reading after the anchor minus <see cref="StartGlucose"/>; negative if glucose
    /// only fell. Rise only.
    /// </summary>
    public double? PeakRise { get; set; }

    /// <summary>Rise only.</summary>
    public double? MinutesToPeak { get; set; }

    /// <summary>The reading nearest two hours after the anchor, within 5 minutes.</summary>
    public double? GlucoseAt2Hours { get; set; }

    public double? GlucoseAt4Hours { get; set; }

    /// <summary>
    /// Minutes from the anchor until glucose came back within
    /// <see cref="EventResponseReport.BackToStartMgdl"/> of the start after its
    /// furthest excursion; 0 if it never left that band, null if it had not come back by the
    /// window's end. Rise only.
    /// </summary>
    public double? MinutesBackToStart { get; set; }

    /// <summary>
    /// <see cref="StartGlucose"/> minus the reading nearest the window's end, within 5 minutes, as
    /// observed ISF reads a correction's drop. Fall only.
    /// </summary>
    public double? Drop { get; set; }

    /// <summary>The lowest reading after the anchor, up to and including the window's end. Fall only.</summary>
    public double? Nadir { get; set; }

    /// <summary>Fall only.</summary>
    public double? MinutesToNadir { get; set; }

    /// <summary>
    /// Whether <see cref="Nadir"/> was below range, 70 mg/dL; null where no reading came. Fall only.
    /// </summary>
    public bool? WentBelowRange { get; set; }

    /// <summary>Delivered basal and every bolus over the whole window.</summary>
    public double? TotalInsulin { get; set; }

    /// <summary>Net insulin above schedule over the whole window.</summary>
    public double? NetInsulin { get; set; }

    public double? IobAtAnchor { get; set; }

    public double? IobAtEnd { get; set; }

    /// <summary>A meal's grams over the net insulin above schedule in the window; null where that was not positive.</summary>
    public double? GramsPerUnit { get; set; }

    /// <summary>
    /// mg/dL per U, <see cref="Drop"/> over <see cref="NetInsulin"/>; read, as grams per unit is, only
    /// off an isolated event, and null where net insulin is under 0.5 U or the event went below
    /// 70 mg/dL, where treatment and a suspended loop shrink the drop. Fall only.
    /// </summary>
    public double? DropPerUnit { get; set; }

    /// <summary>Net insulin above schedule in the first 30 minutes after the anchor.</summary>
    public double? NetInsulinFirst30 { get; set; }

    public double? NetInsulinFirst60 { get; set; }

    public double? NetInsulinFirst120 { get; set; }
}

/// <summary>Each <see cref="EventMetrics"/> figure spread across a cohort's events.</summary>
public class EventMetricsSummary
{
    public LoopAnalysisAggregate? StartGlucose { get; set; }
    public LoopAnalysisAggregate? PeakRise { get; set; }
    public LoopAnalysisAggregate? MinutesToPeak { get; set; }
    public LoopAnalysisAggregate? GlucoseAt2Hours { get; set; }
    public LoopAnalysisAggregate? GlucoseAt4Hours { get; set; }
    public LoopAnalysisAggregate? MinutesBackToStart { get; set; }
    public LoopAnalysisAggregate? Drop { get; set; }
    public LoopAnalysisAggregate? Nadir { get; set; }
    public LoopAnalysisAggregate? MinutesToNadir { get; set; }
    public LoopAnalysisAggregate? TotalInsulin { get; set; }
    public LoopAnalysisAggregate? NetInsulin { get; set; }
    public LoopAnalysisAggregate? IobAtAnchor { get; set; }
    public LoopAnalysisAggregate? IobAtEnd { get; set; }
    public LoopAnalysisAggregate? GramsPerUnit { get; set; }
    public LoopAnalysisAggregate? DropPerUnit { get; set; }
    public LoopAnalysisAggregate? NetInsulinFirst30 { get; set; }
    public LoopAnalysisAggregate? NetInsulinFirst60 { get; set; }
    public LoopAnalysisAggregate? NetInsulinFirst120 { get; set; }
}
