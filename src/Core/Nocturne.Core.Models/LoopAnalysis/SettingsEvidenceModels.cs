using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>A local time-of-day block the ISF and carb ratio evidence is reported in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceBlock>))]
public enum SettingsEvidenceBlock
{
    /// <summary>00:00 to 06:00.</summary>
    Overnight,

    /// <summary>06:00 to 11:00.</summary>
    Morning,

    /// <summary>11:00 to 17:00.</summary>
    Midday,

    /// <summary>17:00 to 24:00.</summary>
    Evening,
}

[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceConfidence>))]
public enum SettingsEvidenceConfidence
{
    Low,
    Moderate,
    High,
}

/// <summary>Why a figure's confidence is below <see cref="SettingsEvidenceConfidence.High"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceConfidenceReason>))]
public enum SettingsEvidenceConfidenceReason
{
    FewEpisodes,

    /// <summary>The interquartile range is wide against the observed value.</summary>
    WideSpread,

    /// <summary>
    /// Glucose change converted at an ISF from the profile, for want of a confident observed one,
    /// where that change carries a material share of the figure.
    /// </summary>
    IsfDependent,

    /// <summary>
    /// Insulin measured above the profile's basal schedule, or above a basal need borrowed from other
    /// blocks, for want of a confident observed basal need for the episode's own time.
    /// </summary>
    BasalDependent,

    /// <summary>
    /// Much of the insulin the window's own doses added was still on board at its end, so the IOB
    /// curve carries the figure.
    /// </summary>
    ResidualIob,

    /// <summary>Most candidates had another intake or bolus in their window: those that qualified may not be typical.</summary>
    MostNotIsolated,

    /// <summary>
    /// Few of the range's days hold a carb entry, so fasting rests on spotting unlogged meals by
    /// glucose alone.
    /// </summary>
    FastingUnverifiable,

    /// <summary>
    /// The figure points towards more insulin than the profile and a borrowed input carries a
    /// material share of it, so its confidence is one level lower than its other reasons give.
    /// </summary>
    MoreInsulinOnBorrowedInput,

    /// <summary>
    /// Glucose was typically still moving at the window's end, so insulin or carbs were still acting
    /// when it closed and the window missed part of the response.
    /// </summary>
    StillMovingAtEnd,

    /// <summary>
    /// Basal: the insulin carried into and out of the blocks, which the IOB curve gives, carries a
    /// material share of the need.
    /// </summary>
    CarriedInsulinMaterial,

    /// <summary>
    /// ISF: a material share of the corrections that reached the low check went low and were
    /// rejected (<see cref="SettingsEvidenceRule.WentLow"/>). They are the corrections that dropped
    /// furthest per unit, so the median of the rest reads low, towards more insulin.
    /// </summary>
    ManyWentLow,
}

/// <summary>Why a figure has no observed value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceUnavailableReason>))]
public enum SettingsEvidenceUnavailableReason
{
    /// <summary>Nothing in the range could be an episode: no correction boluses, or no time in the block.</summary>
    NoCandidates,

    /// <summary>No carb entries in the block, as for a loop run without announcing meals.</summary>
    NoAnnouncedMeals,

    /// <summary>Every announced meal had another meal, bolus or rise in its window.</summary>
    NoneIsolated,

    /// <summary>Candidates existed, and every one was rejected; see the rejection counts.</summary>
    NoneQualified,

    /// <summary>Too few episodes qualified for a median and spread to mean anything.</summary>
    TooFew,

    /// <summary>Glucose moved too far across the block for the basal it needed to be read off it.</summary>
    SteepTrend,

    /// <summary>The loop's correction pointed different ways on too many days.</summary>
    Inconsistent,
}

/// <summary>An exclusion rule an episode can be rejected by. Each episode is counted against the first it fails.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceRule>))]
public enum SettingsEvidenceRule
{
    /// <summary>The loop was open for some of the window.</summary>
    OpenLoop,

    Override,
    TemporaryTarget,
    Exercise,
    Illness,

    /// <summary>Some of the window falls outside the report's filter: a weekday, a profile, or a masked span.</summary>
    Filtered,

    /// <summary>Some of the window falls outside the range: before its start or past its end.</summary>
    OutsideRange,

    /// <summary>Basal: carbs in the window or the hours before it.</summary>
    CarbsBefore,

    /// <summary>
    /// Basal: a meal-like glucose climb no carb entry caused, in the window or the hours before it:
    /// an intake that was not logged.
    /// </summary>
    RiseBefore,

    /// <summary>Basal: a manual bolus in the window or the hours before it.</summary>
    ManualBolus,

    /// <summary>Basal: carbs on board in the window.</summary>
    CarbsOnBoard,

    /// <summary>Basal: a manual temp basal or a suspend in the window.</summary>
    ManualDelivery,

    /// <summary>Basal: delivery in the window that was inferred rather than recorded differed from the schedule.</summary>
    InferredDelivery,

    /// <summary>ISF: carbs from an hour before the bolus to three hours after it.</summary>
    CarbsNear,

    /// <summary>ISF: no target range is known at the bolus.</summary>
    NoTarget,

    /// <summary>ISF: glucose at the bolus was not above the target range.</summary>
    NotAboveTarget,

    /// <summary>ISF: too much insulin was already on board at the bolus.</summary>
    IobAtBolus,

    /// <summary>No IOB is known at the start or end of the window.</summary>
    IobUnknown,

    /// <summary>
    /// The IOB at the window's start and the IOB at its end came from different sources, one the
    /// device's and one calculated, so their difference is not insulin that acted.
    /// </summary>
    IobSourceChanged,

    /// <summary>
    /// ISF: glucose went below 70 mg/dL in the window, so the correction is rejected: a low brings a
    /// suspend and treatment, and the drop is no longer the insulin's alone.
    /// </summary>
    WentLow,

    /// <summary>Another meal, correction bolus or meal-like climb fell in the window.</summary>
    NotIsolated,

    /// <summary>Carb ratio: glucose at the meal was out of range.</summary>
    StartOutOfRange,

    /// <summary>Glucose was moving in the quarter-hour before the meal or correction.</summary>
    StartUnstable,

    /// <summary>No reading stands for glucose at the start or end of the window.</summary>
    MissingGlucose,

    /// <summary>The pump's records cover too little of the window's basal to total its insulin.</summary>
    InsulinUncovered,

    /// <summary>Too little insulin acted, or was needed, for a ratio to it to be read.</summary>
    TooLittleInsulin,
}

public class SettingsEvidenceRejection
{
    public SettingsEvidenceRule Rule { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// One observed setting beside the profile's: an observation from the user's own data, never an
/// instruction.
/// </summary>
public class SettingsEvidenceFigure
{
    /// <summary>Local hour the block starts at.</summary>
    public int StartHour { get; set; }

    /// <summary>Local hour the block ends at, exclusive.</summary>
    public int EndHour { get; set; }

    /// <summary>The observed value; null when <see cref="UnavailableReason"/> is set.</summary>
    public double? Observed { get; set; }

    /// <summary>The profile's value over the block: the median of the value in force across its time in the range.</summary>
    public double? Profile { get; set; }

    /// <summary>Episodes that qualified.</summary>
    public int N { get; set; }

    /// <summary>The episodes' values spread; null when <see cref="UnavailableReason"/> is set.</summary>
    public LoopAnalysisAggregate? Spread { get; set; }

    /// <summary>Null when <see cref="UnavailableReason"/> is set.</summary>
    public SettingsEvidenceConfidence? Confidence { get; set; }

    public List<SettingsEvidenceConfidenceReason> ConfidenceReasons { get; set; } = [];

    public SettingsEvidenceUnavailableReason? UnavailableReason { get; set; }

    /// <summary>
    /// The observed value, read against the profile's, would mean more insulin: a basal above it,
    /// an ISF or carb ratio below it, by at least the direction deadband. False where there is no
    /// observed value.
    /// </summary>
    public bool TowardsMoreInsulin { get; set; }

    /// <summary>
    /// The opposite of <see cref="TowardsMoreInsulin"/>, by the same deadband. Neither is set where the
    /// observed value is within the deadband of the profile's, or there is none.
    /// </summary>
    public bool TowardsLessInsulin { get; set; }

    /// <summary>
    /// Some episode's glucose change was converted at the profile's ISF, scaled by what the other
    /// blocks observed where any did, for want of a confident observed ISF for its own block. Never
    /// set on the ISF itself.
    /// </summary>
    public bool IsfDependent { get; set; }

    /// <summary>Every rule that applies to the figure, with the episodes it rejected, zero included.</summary>
    public List<SettingsEvidenceRejection> Rejections { get; set; } = [];
}

/// <summary>An episode a figure was read from, for drill-down to Day in Review.</summary>
public class SettingsEvidenceEpisode
{
    public DateTime At { get; set; }

    public DateTime End { get; set; }

    /// <summary>The local date, yyyy-MM-dd, as Day in Review's <c>date</c> parameter reads it.</summary>
    public string Date { get; set; } = "";

    /// <summary>The local time, HH:mm, as Day in Review's <c>time</c> parameter reads it.</summary>
    public string Time { get; set; } = "";

    /// <summary>The episode's own observed value, in the figure's unit.</summary>
    public double Value { get; set; }

    /// <summary>The profile's value in force at the episode.</summary>
    public double? Profile { get; set; }
}

/// <summary>
/// Basal evidence for one two-hour local block, U/h. A block's insulin is attributed by when it was
/// delivered, not when it acted.
/// </summary>
public class BasalEvidenceBlock : SettingsEvidenceFigure
{
    /// <summary>U/h: the algorithm's temp delta plus its automatic boluses, the mean across qualifying days.</summary>
    public double? MeanNetLoopCorrection { get; set; }

    /// <summary>The share of qualifying days whose correction had the majority's sign, 0 to 1.</summary>
    public double? Consistency { get; set; }

    /// <summary>mg/dL per hour across the block, spread across qualifying days.</summary>
    public LoopAnalysisAggregate? GlucoseTrend { get; set; }

    public List<BasalEvidenceDay> Days { get; set; } = [];
}

/// <summary>One qualifying day of a basal block; <see cref="SettingsEvidenceEpisode.Value"/> is its observed need, U/h.</summary>
public class BasalEvidenceDay : SettingsEvidenceEpisode
{
    /// <summary>Hours the block lasted that day: 1 or 3 across a DST change.</summary>
    public double Hours { get; set; }

    /// <summary>U/h.</summary>
    public double NetLoopCorrection { get; set; }

    /// <summary>
    /// U/h: the insulin that acted in the block less the correction delivered in it. Insulin
    /// delivered before the block that acted in it, less insulin delivered in it that acts after.
    /// </summary>
    public double CarriedInsulin { get; set; }

    /// <summary>mg/dL, the end of the block minus its start.</summary>
    public double GlucoseChange { get; set; }

    /// <summary>The ISF the glucose change was converted at, mg/dL per U.</summary>
    public double Isf { get; set; }

    /// <summary>See <see cref="SettingsEvidenceFigure.IsfDependent"/>.</summary>
    public bool IsfDependent { get; set; }
}

/// <summary>A figure read off the insulin that acted over each episode's window.</summary>
public class InsulinActedFigure : SettingsEvidenceFigure
{
    /// <summary>
    /// Some episode's insulin was measured above the profile's basal schedule, or above a basal need
    /// borrowed from other blocks, for want of a confident observed basal need for its own time: a
    /// schedule that is short credits the episodes with the loop's top-up.
    /// </summary>
    public bool BasalDependent { get; set; }
}

/// <summary>Observed ISF for one block, mg/dL per U.</summary>
public class IsfEvidenceBlock : InsulinActedFigure
{
    public SettingsEvidenceBlock Block { get; set; }

    public List<IsfEvidenceEpisode> Episodes { get; set; } = [];
}

/// <summary>An episode read off the insulin that acted over its window.</summary>
public class InsulinActedEpisode : SettingsEvidenceEpisode
{
    /// <summary>At the start of the window: the bolus, or a quarter-hour before the meal.</summary>
    public double StartGlucose { get; set; }

    /// <summary>At the end of the window, not the nadir.</summary>
    public double EndGlucose { get; set; }

    /// <summary>U, net insulin above schedule delivered over the window.</summary>
    public double NetInsulin { get; set; }

    /// <summary>
    /// The insulin was measured above the schedule, or above a basal need borrowed from other blocks,
    /// for some of the window; see <see cref="SettingsEvidenceConfidenceReason.BasalDependent"/>.
    /// </summary>
    public bool BasalDependent { get; set; }

    public double IobAtStart { get; set; }

    public double IobAtEnd { get; set; }

    /// <summary>
    /// U: IOB at the window's end above what was on board before its own doses, the share of them
    /// still to act; zero where IOB fell.
    /// </summary>
    public double ResidualIob { get; set; }

    /// <summary>mg/dL a minute, glucose's change over the half-hour before the window's end; null where a reading is missing.</summary>
    public double? EndTrend { get; set; }

    /// <summary>
    /// U: the observed basal need above the schedule over the window, which the loop's top-up met
    /// rather than the episode; 0 when no observed basal need was confident.
    /// </summary>
    public double BasalExcess { get; set; }

    /// <summary>
    /// U: <see cref="NetInsulin"/> plus <see cref="IobAtStart"/>, less <see cref="IobAtEnd"/> and
    /// <see cref="BasalExcess"/>.
    /// </summary>
    public double InsulinActed { get; set; }
}

/// <summary>One correction; <see cref="SettingsEvidenceEpisode.Value"/> is its observed ISF, mg/dL per U.</summary>
public class IsfEvidenceEpisode : InsulinActedEpisode
{
    /// <summary>U, the manual bolus.</summary>
    public double Units { get; set; }

    /// <summary>mg/dL, start minus end; not the nadir.</summary>
    public double Drop { get; set; }
}

/// <summary>Observed carb ratio for one block, g per U.</summary>
public class CarbRatioEvidenceBlock : InsulinActedFigure
{
    public SettingsEvidenceBlock Block { get; set; }

    public List<CarbRatioEvidenceEpisode> Episodes { get; set; } = [];
}

/// <summary>One isolated meal; <see cref="SettingsEvidenceEpisode.Value"/> is its observed carb ratio, g per U.</summary>
public class CarbRatioEvidenceEpisode : InsulinActedEpisode
{
    public double Grams { get; set; }

    /// <summary>
    /// U: <see cref="InsulinActedEpisode.InsulinActed"/> plus the end glucose less the start over
    /// <see cref="Isf"/>.
    /// </summary>
    public double InsulinNeeded { get; set; }

    /// <summary>The ISF the glucose change was converted at, mg/dL per U.</summary>
    public double Isf { get; set; }

    /// <summary>See <see cref="SettingsEvidenceFigure.IsfDependent"/>.</summary>
    public bool IsfDependent { get; set; }
}

/// <summary>The Settings evidence page for one range. Glucose is mg/dL throughout.</summary>
public class SettingsEvidenceRange
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>Twelve two-hour blocks from local midnight.</summary>
    public List<BasalEvidenceBlock> Basal { get; set; } = [];

    /// <summary>One per <see cref="SettingsEvidenceBlock"/>, in its order.</summary>
    public List<IsfEvidenceBlock> Isf { get; set; } = [];

    /// <summary>One per <see cref="SettingsEvidenceBlock"/>, in its order.</summary>
    public List<CarbRatioEvidenceBlock> CarbRatio { get; set; } = [];
}

/// <summary>The Settings evidence page: the primary range and, when asked for, a comparison.</summary>
public class SettingsEvidenceReport : LoopAnalysisResponse
{
    /// <summary>Null when the page is withheld (<see cref="WithheldReason"/>) or no timezone resolved.</summary>
    public SettingsEvidenceRange? Primary { get; set; }

    public SettingsEvidenceRange? Comparison { get; set; }

    /// <summary>Why the page is withheld whole, when it is; nothing is computed then.</summary>
    public SettingsEvidenceWithheldReason? WithheldReason { get; set; }

    /// <summary>The thresholds the rules and windows apply, for the copy that names them.</summary>
    public SettingsEvidenceRules Rules { get; set; } = new();
}

/// <summary>Why the Settings evidence page is withheld whole.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceWithheldReason>))]
public enum SettingsEvidenceWithheldReason
{
    /// <summary>
    /// The caller lacks glucose.read, treatments.read or therapy.read;
    /// <see cref="LoopAnalysisSeries.SettingsEvidence"/> is in <see cref="LoopAnalysisResponse.WithheldSeries"/>.
    /// </summary>
    ReadScopes,

    /// <summary>
    /// The caller reads only the last 24 hours. Every rule looks back hours before its window, and
    /// before the clamp it would see nothing and qualify what it should reject.
    /// </summary>
    HistoryClamped,
}

/// <summary>The thresholds behind the rules' and windows' wording, in milliseconds and mg/dL.</summary>
public class SettingsEvidenceRules
{
    /// <summary>Basal: how long before a block no carbs or meal-like rise may fall.</summary>
    public long BasalCarbFreeMs { get; set; }

    /// <summary>Basal: how long before a block no manual bolus may fall.</summary>
    public long BasalManualBolusFreeMs { get; set; }

    /// <summary>ISF: how long before a correction no carbs may fall.</summary>
    public long IsfCarbFreeBeforeMs { get; set; }

    /// <summary>ISF: the shortest window read, and how long after a correction no carbs may fall.</summary>
    public long IsfMinimumWindowMs { get; set; }

    /// <summary>ISF: the longest window read.</summary>
    public long IsfWindowMs { get; set; }

    /// <summary>Carb ratio: the window read after a meal.</summary>
    public long CarbRatioWindowMs { get; set; }

    /// <summary>Glucose below which an ISF episode is rejected.</summary>
    public double LowMgdl { get; set; }

    /// <summary>The range a carb ratio episode's glucose must start in.</summary>
    public double StartLowMgdl { get; set; }

    public double StartHighMgdl { get; set; }
}
