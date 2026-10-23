using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>
/// Spread of one series in one slot: across days for a per-day value, or across readings for
/// pooled glucose. A series with no value in the slot is absent rather than an aggregate of zeros.
/// </summary>
public class LoopAnalysisAggregate
{
    public double P10 { get; set; }
    public double P25 { get; set; }
    public double P50 { get; set; }
    public double P75 { get; set; }
    public double P90 { get; set; }
    public double Mean { get; set; }

    /// <summary>Values summarised: days, or readings for glucose.</summary>
    public int N { get; set; }

    /// <summary>Fewer days contributed than the threshold below which the spread is not to be read as a pattern.</summary>
    public bool Sparse { get; set; }
}

/// <summary>The Daily profile page: the primary range and, when asked for, a comparison range.</summary>
public class DailyProfileResponse : LoopAnalysisResponse
{
    public DailyProfileRange Primary { get; set; } = new();

    public DailyProfileRange? Comparison { get; set; }
}

/// <summary>288 local five-minute slots of one range, and the days that went into them.</summary>
public class DailyProfileRange
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public int SlotMinutes { get; set; }

    public List<DailyProfileSlot> Slots { get; set; } = [];

    /// <summary>Net loop correction delivered in each local hour of the day, in U, mean across days.</summary>
    public List<LoopAnalysisHourlyValue> HourlyNetLoopCorrection { get; set; } = [];

    /// <summary>Every local day of the range with analysed time, oldest first.</summary>
    public List<DailyProfileDay> Days { get; set; } = [];

    public DailyProfileSummary Summary { get; set; } = new();
}

public class DailyProfileSlot
{
    public int Slot { get; set; }

    /// <summary>Minutes after local midnight the slot starts at.</summary>
    public int MinuteOfDay { get; set; }

    /// <summary>mg/dL, every reading in the slot pooled across days.</summary>
    public LoopAnalysisAggregate? Glucose { get; set; }

    /// <summary>U/h, the schedule including any override's scale factor.</summary>
    public LoopAnalysisAggregate? ScheduledBasal { get; set; }

    /// <summary>U/h.</summary>
    public LoopAnalysisAggregate? DeliveredBasal { get; set; }

    public NetLoopCorrectionAggregate? NetLoopCorrection { get; set; }

    /// <summary>U/h above schedule from manual temp basals.</summary>
    public LoopAnalysisAggregate? ManualTemp { get; set; }

    /// <summary>U/h above schedule while suspended; never positive.</summary>
    public LoopAnalysisAggregate? Suspend { get; set; }

    /// <summary>U/h above schedule with no temp running; see <c>NetInsulinOrigin.Inferred</c>.</summary>
    public LoopAnalysisAggregate? Inferred { get; set; }

    /// <summary>U.</summary>
    public LoopAnalysisAggregate? Iob { get; set; }

    /// <summary>The source that supplied most of the slot's IOB.</summary>
    public IobCobSource? IobSource { get; set; }

    /// <summary>g.</summary>
    public LoopAnalysisAggregate? Cob { get; set; }

    public IobCobSource? CobSource { get; set; }

    /// <summary>U.</summary>
    public DailyProfileEventSeries? ManualBoluses { get; set; }

    /// <summary>g.</summary>
    public DailyProfileEventSeries? Carbs { get; set; }

    /// <summary>
    /// mg/dL, the lower bound of the target range in force: a temporary target where one ran, else
    /// the schedule. The median across the days the slot was analysed on.
    /// </summary>
    public double? TargetLow { get; set; }

    /// <summary>mg/dL, as <see cref="TargetLow"/>.</summary>
    public double? TargetHigh { get; set; }
}

/// <summary>
/// Net loop correction in one slot, U/h: the algorithm's temp-basal delta against schedule plus
/// its automatic boluses.
/// </summary>
public class NetLoopCorrectionAggregate
{
    /// <summary>The headline: mean across days of temp delta plus automatic boluses.</summary>
    public double Mean { get; set; }

    /// <summary>
    /// The algorithm temp-basal delta alone. The spread is taken on this rather than on
    /// <see cref="Mean"/>'s sum because automatic boluses are spiky: their median in a slot is
    /// about zero, so percentiles over the sum describe the boluses' absence.
    /// </summary>
    public LoopAnalysisAggregate Rate { get; set; } = new();

    public double AutomaticBolusMean { get; set; }
}

/// <summary>
/// Doses or carbs in one slot. Both figures are per standard five-minute slot of analysed time, so
/// a fall-back day's repeated slots count once.
/// </summary>
public class DailyProfileEventSeries
{
    public double MeanPerDay { get; set; }

    public double EventsPerDay { get; set; }

    /// <summary>Days the slot was analysed on.</summary>
    public int N { get; set; }

    public bool Sparse { get; set; }
}

public class LoopAnalysisHourlyValue
{
    public int Hour { get; set; }

    /// <summary>Null when no day covered the hour.</summary>
    public double? Mean { get; set; }

    public int N { get; set; }

    public bool Sparse { get; set; }
}

public class DailyProfileDay
{
    /// <summary>The local date, yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>When the local day begins.</summary>
    public DateTime Start { get; set; }

    /// <summary>
    /// The range covers the whole day. False for a day cut short: today, analysed up to now, the same
    /// part of a comparison day, or a first day a 24-hour clamp starts partway through.
    /// </summary>
    public bool Complete { get; set; }

    public DailyProfileDayCoverage Coverage { get; set; } = new();

    /// <summary>U, over the day's covered time.</summary>
    public double? NetLoopCorrection { get; set; }

    /// <summary>U, delivered basal and every bolus.</summary>
    public double? TotalInsulin { get; set; }

    /// <summary>U, the basal the pump's records account for.</summary>
    public double? DeliveredBasal { get; set; }

    /// <summary>U.</summary>
    public double? AutomaticBolus { get; set; }

    /// <summary>U.</summary>
    public double? ManualBolus { get; set; }

    /// <summary>Glucose readings were analysed on the day; read by the scope guard, not served.</summary>
    [JsonIgnore]
    public bool HasGlucose { get; set; }

    /// <summary>Basal, doses or carbs were recorded on the day; read by the scope guard, not served.</summary>
    [JsonIgnore]
    public bool HasTreatments { get; set; }
}

/// <summary>
/// Which of the day's 288 slots each series counted in, as base64 of a 36-byte bitmap: slot
/// <c>i</c> is bit <c>i % 8</c> (least significant first) of byte <c>i / 8</c>. Null for a series
/// withheld from the caller.
/// </summary>
public class DailyProfileDayCoverage
{
    /// <summary>At least half the slot was analysed, after the filter's masks.</summary>
    public string? Analysed { get; set; }

    public string? Glucose { get; set; }

    public string? ScheduledBasal { get; set; }

    public string? DeliveredBasal { get; set; }

    public string? NetLoopCorrection { get; set; }

    /// <summary>IOB and COB, which come from the same ticks and so cover the same slots.</summary>
    public string? IobCob { get; set; }
}

/// <summary>
/// Only the figures no other report returns; the strip around them composes
/// <c>insulin-delivery-stats</c> and <c>daily-basal-bolus-ratios</c>.
/// </summary>
public class DailyProfileSummary
{
    /// <summary>
    /// Days with any analysed glucose reading, dose or carb entry, or basal delivery the pump's
    /// records account for; for a caller who cannot read glucose or treatments, only the days with
    /// data it can read.
    /// </summary>
    public int DaysWithData { get; set; }

    /// <summary>Delivered over scheduled basal, over the time both are known.</summary>
    public double? DeliveredToScheduledRatio { get; set; }

    /// <summary>
    /// U per day, over the whole days <see cref="Insulin"/> counts that also had a schedule
    /// throughout; nothing is scaled.
    /// </summary>
    public double? NetLoopCorrectionPerDay { get; set; }

    /// <summary>Net loop correction as a percentage of total insulin, over the same days.</summary>
    public double? NetLoopCorrectionTddPercent { get; set; }

    /// <summary>Days that counted toward the two net loop correction figures.</summary>
    public int NetLoopCorrectionDays { get; set; }

    /// <summary>Total insulin per day and its split; null when no day's basal was covered.</summary>
    public DailyInsulinSummary? Insulin { get; set; }
}

/// <summary>
/// Insulin per day over the near-complete days only: those analysed, and covered by the basal the
/// pump's records account for, through at least <c>DailyProfileCalculator.InsulinSummaryMinimumCoverage</c>
/// of their length. Nothing is scaled; a masked or partly recorded day is left out rather than
/// counted as a whole one.
/// </summary>
/// <remarks>
/// In the basal/bolus split an automatic bolus (SMB) is bolus, as Trio and AAPS count it in their
/// total daily dose; basal is only the basal delivered. Net loop correction is a different measure
/// and still adds automatic boluses to the algorithm's temp delta.
/// </remarks>
public class DailyInsulinSummary
{
    /// <summary>U/day, spread across days; its N is the number of days counted.</summary>
    public LoopAnalysisAggregate Total { get; set; } = new();

    /// <summary>U/day.</summary>
    public double DeliveredBasal { get; set; }

    /// <summary>U/day.</summary>
    public double AutomaticBolus { get; set; }

    /// <summary>U/day.</summary>
    public double ManualBolus { get; set; }

    /// <summary>U/day, automatic and manual boluses together.</summary>
    public double Bolus { get; set; }

    /// <summary>Percent of <see cref="LoopAnalysisAggregate.Mean"/> total; with <see cref="BolusPercent"/>, the basal/bolus split.</summary>
    public double DeliveredBasalPercent { get; set; }

    /// <summary>Percent of total; <see cref="AutomaticBolusPercent"/> plus <see cref="ManualBolusPercent"/>.</summary>
    public double BolusPercent { get; set; }

    public double AutomaticBolusPercent { get; set; }

    public double ManualBolusPercent { get; set; }
}
