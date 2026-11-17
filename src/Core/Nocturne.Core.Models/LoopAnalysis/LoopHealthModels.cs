using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>The Loop health page: the primary range and, when asked for, a comparison range.</summary>
public class LoopHealthResponse : LoopAnalysisResponse
{
    public LoopHealthRange Primary { get; set; } = new();

    public LoopHealthRange? Comparison { get; set; }

    /// <summary>Minutes between loop cycles beyond which the time between them is a loop gap.</summary>
    public int LoopGapMinutes { get; set; }
}

/// <summary>
/// How the loop ran over one range. Every figure is over the analysed time (the range less the
/// filter's masks) and is given for the range and for each local hour of the day. A figure is null
/// where it does not apply or the caller's scopes withhold it.
/// </summary>
public class LoopHealthRange
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>
    /// Every local day of the range, oldest first, analysed or not: which days the masks emptied
    /// would show where spans of a category the caller may lack fell.
    /// </summary>
    public List<LoopHealthDay> Days { get; set; } = [];

    public LoopHealthActivity Activity { get; set; } = new();

    public LoopHealthCadence Cadence { get; set; } = new();

    public LoopHealthZeroDelivery? ZeroDelivery { get; set; }

    public LoopHealthMaxBasal? MaxBasal { get; set; }

    /// <summary>
    /// Share of oref loop time spent at <c>max_iob</c>; null where no oref system reported, since
    /// only oref names the limit.
    /// </summary>
    public LoopHealthShare? AtMaxIob { get; set; }

    public LoopHealthAutomaticBoluses? AutomaticBoluses { get; set; }

    /// <summary>
    /// Share of oref suggestions that asked for a temp or a microbolus and were not enacted; null
    /// where no system recorded its suggestions apart from what it enacted.
    /// </summary>
    public LoopHealthShare? SuggestedNotEnacted { get; set; }

    /// <summary>Autosens or dynamic ISF ratio, every loop cycle's value pooled.</summary>
    public LoopHealthDistribution? SensitivityRatio { get; set; }

    public LoopHealthLimits Limits { get; set; } = new();

    /// <summary>Why each figure that can be unknown is null, where it is and the scopes allow it.</summary>
    public LoopHealthUnknown Unknown { get; set; } = new();
}

/// <summary>
/// Why a Loop health figure is null. Each is set only where its figure is null for want of data,
/// and is withheld with its figure.
/// </summary>
public class LoopHealthUnknown
{
    public LoopHealthUnknownReason? LoopGaps { get; set; }

    public LoopHealthUnknownReason? UnrecordedDelivery { get; set; }

    /// <summary>Why no algorithm temp's cap is known, so <see cref="LoopHealthMaxBasal.AtCap"/> has no figure.</summary>
    public LoopHealthUnknownReason? MaxBasal { get; set; }

    public LoopHealthUnknownReason? AtMaxIob { get; set; }

    public LoopHealthUnknownReason? SuggestedNotEnacted { get; set; }

    public LoopHealthUnknownReason? LoopMaximumBasalRate { get; set; }

    public LoopHealthUnknownReason? OrefMaxIob { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<LoopHealthUnknownReason>))]
public enum LoopHealthUnknownReason
{
    /// <summary>
    /// The system that looped does not report it: Loop records no <c>max_iob</c> and no
    /// suggestion apart from what it enacted, and oref (OpenAPS, AAPS, Trio, iAPS) has no maximum
    /// basal rate setting of Loop's kind.
    /// </summary>
    NotRecordedByThisSystem,

    /// <summary>No APS loop cycle was recorded in the range.</summary>
    NoCyclesInRange,

    /// <summary>The cycles stored no oref suggestion, or none whose reason named the figure.</summary>
    NoReasonRecorded,

    /// <summary>What it is measured on fell only in time the filter's masks left out.</summary>
    MaskedOut,

    /// <summary>The profile in force records no Loop maximum basal rate.</summary>
    SettingNotUploaded,

    /// <summary>The algorithm set no temp basal in the range.</summary>
    NoAlgorithmTemps,

    /// <summary>
    /// oref cycles ran in the range, but none enacted a temp's rate within five minutes of its start
    /// and the temp names no snapshot that set it, so no reason speaks for it.
    /// </summary>
    SetterNotFound,
}

public class LoopHealthDay
{
    /// <summary>The local date, yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>When the local day begins.</summary>
    public DateTime Start { get; set; }
}

/// <summary>A share of time or of events, %, for the range and for each local hour of the day.</summary>
public class LoopHealthShare
{
    /// <summary>Pooled over the range; null where nothing was measured.</summary>
    public double? Percent { get; set; }

    /// <summary>Each day's share, across the days at least half analysed.</summary>
    public LoopAnalysisAggregate? Daily { get; set; }

    /// <summary>The 24 local hours, midnight first.</summary>
    public List<LoopHealthShareHour> Hours { get; set; } = [];
}

public class LoopHealthShareHour
{
    public int Hour { get; set; }

    public double? Percent { get; set; }

    /// <summary>The hour's share on each day the hour was at least half analysed.</summary>
    public LoopAnalysisAggregate? Daily { get; set; }
}

/// <summary>A value's spread for the range and for each local hour of the day.</summary>
public class LoopHealthDistribution
{
    public LoopAnalysisAggregate? Overall { get; set; }

    /// <summary>The 24 local hours, midnight first.</summary>
    public List<LoopHealthHourValue> Hours { get; set; } = [];
}

public class LoopHealthHourValue
{
    public int Hour { get; set; }

    public LoopAnalysisAggregate? Value { get; set; }
}

/// <summary>Time the loop, the pump and the CGM were running.</summary>
/// <remarks>
/// The three percentages are <c>aid-system-metrics</c>' over the whole range, without the filter's
/// masks. The shares split the same measures by hour over the analysed time: an enacted loop cycle
/// stands for five minutes of closed loop and any cycle for five minutes of pump use, or, with no
/// APS cycles in the range, any temp basal stands for pump use and an algorithm temp basal for
/// closed loop where its origin was reported or confirmed by a snapshot, not where it is only the
/// uploader's default; a CGM reading stands for its stream's cadence.
/// </remarks>
public class LoopHealthActivity
{
    public double? AidActivePercent { get; set; }

    public double? PumpUsePercent { get; set; }

    public double? CgmActivePercent { get; set; }

    public LoopHealthShare? ClosedLoop { get; set; }

    public LoopHealthShare? Pump { get; set; }

    public LoopHealthShare? Cgm { get; set; }
}

public class LoopHealthCadence
{
    /// <summary>
    /// Stretches longer than fifteen minutes between two APS loop cycles; null, not none, where no
    /// cycle was recorded in the range.
    /// </summary>
    public LoopHealthGaps? LoopGaps { get; set; }

    /// <summary>
    /// Loop time (cycles no more than fifteen minutes apart) where delivery is unknown: no temp
    /// basal is recorded and none bounds a stretch the schedule can be taken to have run through,
    /// so the pump's delivery records are missing while the loop was running. Null where no cycle was
    /// recorded in the range.
    /// </summary>
    public LoopHealthGaps? UnrecordedDelivery { get; set; }
}

/// <summary>
/// Gaps of one kind, each counted once however many days or hours it spans. A gap counts where any
/// of it was analysed, and is reported clipped to the range.
/// </summary>
public class LoopHealthGaps
{
    public int Count { get; set; }

    public double TotalMinutes { get; set; }

    public double? LongestMinutes { get; set; }

    /// <summary>Oldest first.</summary>
    public List<LoopHealthGap> Gaps { get; set; } = [];
}

public class LoopHealthGap
{
    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    public double Minutes { get; set; }

    /// <summary>The local date the gap began on, yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    /// <summary>Minutes after local midnight the gap began.</summary>
    public int StartMinuteOfDay { get; set; }
}

/// <summary>
/// Time with no basal delivered, by origin, as a share of the time delivery is known. A suspend is
/// reported apart from the algorithm's zero temps.
/// </summary>
public class LoopHealthZeroDelivery
{
    public LoopHealthShare AlgorithmZeroTemp { get; set; } = new();

    public LoopHealthShare ManualZeroTemp { get; set; } = new();

    public LoopHealthShare Suspended { get; set; } = new();
}

/// <summary>
/// Time the algorithm's temps delivered at the effective maximum basal rate. Loop's cap is its
/// maximum basal rate setting. oref's is <c>maxSafeBasal</c>, the lowest of <c>max_basal</c> and
/// two safety multiples of the schedule; none of those is stored, so a temp is at it where the
/// cycle that set it says it cut the rate to it.
/// </summary>
public class LoopHealthMaxBasal
{
    /// <summary>Of the algorithm temp time whose cap is known.</summary>
    public LoopHealthShare AtCap { get; set; } = new();

    /// <summary>Of the algorithm temp time: where the cap is known, and so <see cref="AtCap"/> is measured.</summary>
    public LoopHealthShare CapKnown { get; set; } = new();
}

/// <summary>Boluses the algorithm gave (SMBs, Loop's automatic boluses).</summary>
public class LoopHealthAutomaticBoluses
{
    /// <summary>Count per day; by hour, per day in that hour.</summary>
    public LoopHealthDistribution PerDay { get; set; } = new();

    /// <summary>U, every bolus pooled.</summary>
    public LoopHealthDistribution Size { get; set; } = new();
}

/// <summary>The newest value of each limit recorded in the range; null where none was.</summary>
public class LoopHealthLimits
{
    /// <summary>U/h.</summary>
    public double? LoopMaximumBasalRate { get; set; }

    /// <summary>U, as an oref reason reported it when it bound.</summary>
    public double? OrefMaxIob { get; set; }
}
