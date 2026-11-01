using System.Text.Json.Serialization;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

public class BasicGlucoseStats
{
    /// <summary>
    /// Total number of glucose readings
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Mean glucose value, rounded to one decimal place
    /// </summary>
    public double Mean { get; set; }

    public double Median { get; set; }

    /// <summary>
    /// Minimum glucose value recorded
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// Maximum glucose value recorded
    /// </summary>
    public double Max { get; set; }

    /// <summary>
    /// Standard deviation of glucose values, rounded to one decimal place
    /// </summary>
    public double StandardDeviation { get; set; }

    /// <summary>
    /// Percentiles of glucose values
    /// </summary>
    public GlucosePercentiles Percentiles { get; set; } = new();
}

/// <summary>
/// Glucose percentile values
/// </summary>
public class GlucosePercentiles
{
    /// <summary>
    /// 5th percentile
    /// </summary>
    public double P5 { get; set; }

    /// <summary>
    /// 10th percentile
    /// </summary>
    public double P10 { get; set; }

    /// <summary>
    /// 25th percentile (first quartile)
    /// </summary>
    public double P25 { get; set; }

    /// <summary>
    /// 50th percentile (median)
    /// </summary>
    public double P50 { get; set; }

    /// <summary>
    /// 75th percentile (third quartile)
    /// </summary>
    public double P75 { get; set; }

    /// <summary>
    /// 90th percentile
    /// </summary>
    public double P90 { get; set; }

    /// <summary>
    /// 95th percentile
    /// </summary>
    public double P95 { get; set; }
}

public class GlycemicVariability
{
    /// <summary>
    /// Traditional measure of dispersion, standardized for mean; Measures short-term, within-day variability
    /// </summary>
    public double CoefficientOfVariation { get; set; }

    /// <summary>
    /// Traditional measure of dispersion; Measures short-term, within-day variability
    /// </summary>
    public double StandardDeviation { get; set; }

    /// <summary>
    /// Average of all glycemic excursions (except excursion having value less than 1 SD from mean glucose) in a 24 h time period; Captures short-term, within-day variability
    /// </summary>
    public double MeanAmplitudeGlycemicExcursions { get; set; }

    /// <summary>
    /// Standard deviation of summated difference between current observation and previous observation; Captures short-term, within-day variability
    /// </summary>
    public double ContinuousOverlappingNetGlycemicAction { get; set; }

    /// <summary>Null when the tenant has no timezone to bound the days in.</summary>
    public double? AverageDailyRiskRange { get; set; }

    /// <summary>Why <see cref="AverageDailyRiskRange"/> is null; null when a zone was applied.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }

    public double LabilityIndex { get; set; }

    public double JIndex { get; set; }

    /// <summary>
    /// High Blood Glucose Index - risk index for hyperglycemia
    /// </summary>
    public double HighBloodGlucoseIndex { get; set; }

    /// <summary>
    /// Low Blood Glucose Index - risk index for hypoglycemia
    /// </summary>
    public double LowBloodGlucoseIndex { get; set; }

    /// <summary>
    /// Glycemic Variability Index - measures glucose line distance traveled; 1.0-1.2 low, 1.2-1.5 modest, greater than 1.5 high variability
    /// </summary>
    public double GlycemicVariabilityIndex { get; set; }

    /// <summary>
    /// Patient Glycemic Status - combines GVI, mean glucose, and time in range; less than or equal to 35 excellent (non-diabetic), 35-100 good, 100-150 poor, greater than 150 very poor
    /// </summary>
    public double PatientGlycemicStatus { get; set; }

    /// <summary>
    /// Estimated A1C from average glucose
    /// </summary>
    public double EstimatedA1c { get; set; }

    public A1cDisplayValue EstimatedA1cDisplay => A1cDisplayValue.FromPercent(EstimatedA1c);
    public A1cDisplayValue A1cTarget => A1cDisplayValue.FromPercent(GlucoseConstants.A1cTargetPercent);

    /// <summary>
    /// Judged per unit on the value as displayed in that unit, so each state agrees with the figure
    /// shown in its own unit.
    /// </summary>
    public A1cTargetState EstimatedA1cMeetsTarget => A1cTargetState.Below(EstimatedA1cDisplay, A1cTarget);

    public double CoefficientOfVariationTarget => GlucoseConstants.CoefficientOfVariationTargetPercent;

    /// <summary>
    /// Judged on the CV as a whole percent, the way <see cref="CoefficientOfVariation"/> is displayed.
    /// </summary>
    public GlycemicVariabilityStatus CoefficientOfVariationStatus =>
        Math.Round(CoefficientOfVariation, MidpointRounding.AwayFromZero) switch
        {
            <= GlucoseConstants.CoefficientOfVariationTargetPercent => GlycemicVariabilityStatus.Stable,
            <= GlucoseConstants.CoefficientOfVariationVariablePercent => GlycemicVariabilityStatus.Moderate,
            _ => GlycemicVariabilityStatus.Variable,
        };

    /// <summary>
    /// Glucose Management Indicator - modern replacement for estimated A1c
    /// Based on: GMI (%) = 3.31 + (0.02392 x mean glucose in mg/dL)
    /// </summary>
    public GlucoseManagementIndicator? Gmi { get; set; }

    /// <summary>
    /// Mean total daily glucose change in mg/dL - sum of absolute glucose changes divided by number of days
    /// </summary>
    public double MeanTotalDailyChange { get; set; }

    /// <summary>
    /// Percentage of readings where glucose changed more than 15 mg/dL within 5-6 minutes
    /// </summary>
    public double TimeInFluctuation { get; set; }
}

public class GlycemicThresholds
{
    /// <summary>
    /// Threshold for very low glucose (defaults to <see cref="GlucoseConstants.VeryLowMgdl"/>)
    /// </summary>
    public double VeryLow { get; set; } = GlucoseConstants.VeryLowMgdl;

    /// <summary>
    /// Threshold for low glucose (defaults to <see cref="GlucoseConstants.TargetBottomMgdl"/>)
    /// </summary>
    public double Low { get; set; } = GlucoseConstants.TargetBottomMgdl;

    /// <summary>
    /// Target range bottom threshold (defaults to <see cref="GlucoseConstants.TargetBottomMgdl"/>)
    /// </summary>
    public double TargetBottom { get; set; } = GlucoseConstants.TargetBottomMgdl;

    /// <summary>
    /// Target range top threshold (defaults to <see cref="GlucoseConstants.TargetTopMgdl"/>)
    /// </summary>
    public double TargetTop { get; set; } = GlucoseConstants.TargetTopMgdl;

    /// <summary>
    /// Tight target range bottom threshold. The tight band narrows the top only; its bottom is the
    /// same hypoglycaemia boundary as <see cref="GlucoseConstants.TargetBottomMgdl"/>.
    /// </summary>
    public double TightTargetBottom { get; set; } = GlucoseConstants.TargetBottomMgdl;

    /// <summary>
    /// Tight target range top threshold (defaults to <see cref="GlucoseConstants.TightTargetTopMgdl"/>)
    /// </summary>
    public double TightTargetTop { get; set; } = GlucoseConstants.TightTargetTopMgdl;

    /// <summary>
    /// Threshold for high glucose (defaults to <see cref="GlucoseConstants.TargetTopMgdl"/>)
    /// </summary>
    public double High { get; set; } = GlucoseConstants.TargetTopMgdl;

    /// <summary>
    /// Threshold for very high glucose (defaults to <see cref="GlucoseConstants.VeryHighMgdl"/>)
    /// </summary>
    public double VeryHigh { get; set; } = GlucoseConstants.VeryHighMgdl;
}

public class TimeInRangeMetrics
{
    /// <summary>The band edges the percentages, durations and episodes were measured against.</summary>
    public GlycemicThresholds? Thresholds { get; set; }

    /// <summary>
    /// Percentages of time in each range
    /// </summary>
    public TimeInRangePercentages Percentages { get; set; } = new();

    /// <summary>
    /// Durations in each range (in minutes)
    /// </summary>
    public TimeInRangeDurations Durations { get; set; } = new();

    public TimeInRangeEpisodes Episodes { get; set; } = new();

    /// <summary>
    /// Per-range detailed statistics (count, average, median, stdDev)
    /// </summary>
    public TimeInRangeDetailedStats RangeStats { get; set; } = new();

    /// <summary>
    /// Average minutes per covered day in target, below-range and above-range zones
    /// </summary>
    public AverageDailyMinutes AverageDailyMinutes { get; set; } = new();

    /// <summary>
    /// Episodes per covered day
    /// </summary>
    public AverageDailyEpisodes AverageDailyEpisodes { get; set; } = new();
}

/// <summary>
/// Detailed statistics for each glucose range
/// </summary>
public class TimeInRangeDetailedStats
{
    /// <summary>
    /// Statistics for low range (below 70 mg/dL)
    /// </summary>
    public PeriodMetrics Low { get; set; } = new() { PeriodName = "Low" };

    /// <summary>
    /// Statistics for target/in-range (70-180 mg/dL)
    /// </summary>
    public PeriodMetrics Target { get; set; } = new() { PeriodName = "In Range" };

    /// <summary>
    /// Statistics for high range (above 180 mg/dL)
    /// </summary>
    public PeriodMetrics High { get; set; } = new() { PeriodName = "High" };
}

/// <summary>
/// Time in range percentages: the minutes a zone covers over the minutes the readings cover, from
/// the same minutes that back <see cref="TimeInRangeDurations"/>, so the two agree whatever mix of
/// sensor cadences the period holds. A count of readings would weigh a one-minute sensor's
/// reading as much as a five-minute one's. The four outer zones and the time between them sum to
/// 100; <see cref="Target"/> is that time only when the target range starts where low ends.
/// </summary>
public class TimeInRangePercentages
{
    /// <summary>
    /// Percentage of time in very low range (less than 54 mg/dL)
    /// </summary>
    public double VeryLow { get; set; }

    /// <summary>
    /// Percentage of time in low range (54-70 mg/dL)
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// Percentage of time in target range (70-180 mg/dL)
    /// </summary>
    public double Target { get; set; }

    /// <summary>
    /// Percentage of time in tight target range (70-140 mg/dL)
    /// </summary>
    public double TightTarget { get; set; }

    /// <summary>
    /// Percentage of time in high range (180-250 mg/dL)
    /// </summary>
    public double High { get; set; }

    /// <summary>
    /// Percentage of time in very high range (greater than 250 mg/dL)
    /// </summary>
    public double VeryHigh { get; set; }

    /// <summary>
    /// Percentage of time below the target range: very low plus low
    /// </summary>
    public double BelowRange => VeryLow + Low;

    /// <summary>
    /// Percentage of time above the target range: high plus very high
    /// </summary>
    public double AboveRange => High + VeryHigh;
}

/// <summary>
/// Time in each consensus glucose band for one hour of the day. The bands partition the hour's
/// readings on the <see cref="GlycemicThresholds"/> defaults, not a tenant's own targets, and
/// split the target range at the tight-target top so the six can be stacked.
/// </summary>
public class ExtendedTimeInRangePercentages
{
    /// <summary>
    /// Percentage of readings below 54 mg/dL
    /// </summary>
    public double VeryLow { get; set; }

    /// <summary>
    /// Percentage of readings from 54 to below 70 mg/dL
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// Percentage of readings from 70 to 140 mg/dL inclusive (the tight target range)
    /// </summary>
    public double TightTarget { get; set; }

    /// <summary>
    /// Percentage of readings above 140 and up to 180 mg/dL: in range, above the tight target
    /// </summary>
    public double AboveTightTarget { get; set; }

    /// <summary>
    /// Percentage of readings above 180 and up to 250 mg/dL
    /// </summary>
    public double High { get; set; }

    /// <summary>
    /// Percentage of readings above 250 mg/dL
    /// </summary>
    public double VeryHigh { get; set; }
}

/// <summary>
/// Time in range durations (in minutes), measured from the gaps between the readings themselves
/// rather than assuming a cadence, so a one-minute sensor and a five-minute one both report the
/// time they actually covered. A gap the sensor did not cover counts as one extra median gap.
/// <see cref="TimeInRangePercentages"/> is these over the covered minutes.
/// </summary>
public class TimeInRangeDurations
{
    /// <summary>
    /// Duration in very low range (minutes)
    /// </summary>
    public double VeryLow { get; set; }

    /// <summary>
    /// Duration in low range (minutes)
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// Duration in target range (minutes)
    /// </summary>
    public double Target { get; set; }

    /// <summary>
    /// Duration in tight target range (minutes)
    /// </summary>
    public double TightTarget { get; set; }

    /// <summary>
    /// Duration in high range (minutes)
    /// </summary>
    public double High { get; set; }

    /// <summary>
    /// Duration in very high range (minutes)
    /// </summary>
    public double VeryHigh { get; set; }

    /// <summary>
    /// Time above range (minutes): <see cref="High"/> and <see cref="VeryHigh"/> together, which
    /// are mutually excluding zones.
    /// </summary>
    public double AboveRange { get; set; }

    /// <summary>
    /// Time below range (minutes): <see cref="Low"/> and <see cref="VeryLow"/> together.
    /// </summary>
    public double BelowRange { get; set; }
}

/// <summary>
/// Average minutes per day in each zone: the zone minutes of <see cref="TimeInRangeDurations"/>
/// over the days the readings cover, which is the time they stand for over twenty-four hours.
/// A gap the sensor did not cover adds no days, and a period with no covered time reports zero.
/// <see cref="AverageDailyEpisodes"/> divides by the same days.
/// </summary>
public class AverageDailyMinutes
{
    /// <summary>
    /// Average minutes per day in target range
    /// </summary>
    public double Target { get; set; }

    /// <summary>
    /// Average minutes per day below target (very low and low)
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// Average minutes per day above target (high and very high)
    /// </summary>
    public double High { get; set; }
}

/// <summary>
/// Time in range episodes, on the CGM event definition of Battelino et al., "Continuous glucose
/// monitoring and metrics for clinical trials: an international consensus statement", Lancet
/// Diabetes Endocrinol 2023;11:42-57, over the thresholds of the time-in-range consensus (Battelino
/// et al., Diabetes Care 2019;42:1593-1603). An episode begins after at least 15 consecutive
/// minutes beyond the level 1 threshold (below <c>Low</c> or above <c>TargetTop</c>) and ends after
/// at least 15 consecutive minutes back within it. It is a level 2 episode (<see cref="VeryLow"/>
/// or <see cref="VeryHigh"/>) only if it also spent at least 15 consecutive minutes beyond the
/// level 2 threshold; each episode is counted once, at the higher level it reached.
/// <para>
/// A reading stands for the minutes until the next one, up to twice the sensor's local cadence
/// (at most 15 minutes). A stretch without readings longer than both 15 minutes and twice that
/// cadence is a gap: it ends a run rather than bridging it, and the reading before it stands for
/// one cadence.
/// </para>
/// </summary>
public class TimeInRangeEpisodes
{
    public int VeryLow { get; set; }

    public int Low { get; set; }

    public int High { get; set; }

    public int VeryHigh { get; set; }

    /// <summary>
    /// Number of excursions above range, which is <see cref="High"/> plus <see cref="VeryHigh"/>:
    /// every excursion above target is counted once, against the more extreme of the two it
    /// reached.
    /// </summary>
    public int AboveRange { get; set; }

    /// <summary>
    /// Number of excursions below range, which is <see cref="Low"/> plus <see cref="VeryLow"/>,
    /// counted the same way as <see cref="AboveRange"/>.
    /// </summary>
    public int BelowRange { get; set; }
}

/// <summary>
/// <see cref="TimeInRangeEpisodes"/> per covered day, on the days <see cref="AverageDailyMinutes"/>
/// divides by.
/// </summary>
public class AverageDailyEpisodes
{
    public double VeryLow { get; set; }

    public double Low { get; set; }

    public double High { get; set; }

    public double VeryHigh { get; set; }

    public double AboveRange { get; set; }

    public double BelowRange { get; set; }
}

public class DistributionDataPoint
{
    /// <summary>
    /// Range description (e.g., "70-80")
    /// </summary>
    public string Range { get; set; } = string.Empty;

    /// <summary>
    /// Number of readings in this range
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Percentage of total readings in this range
    /// </summary>
    public double Percent { get; set; }
}

public class DistributionBin
{
    public string Range { get; set; } = string.Empty;

    public double Min { get; set; }

    public double Max { get; set; }
}

public class TreatmentSummary
{
    public TreatmentTotals Totals { get; set; } = new();

    /// <summary>
    /// Total number of treatment entries
    /// </summary>
    public int TreatmentCount { get; set; }

    /// <summary>
    /// Number of bolus records those totals came from
    /// </summary>
    public int BolusCount { get; set; }

    /// <summary>
    /// Number of carb intake records those totals came from
    /// </summary>
    public int CarbEntryCount { get; set; }

    /// <summary>
    /// Insulin-to-carb ratio as dosed, as <see cref="InsulinDeliveryStatistics.IcRatio"/>; null when
    /// no manual bolus is paired with a meal
    /// </summary>
    public double? CarbToInsulinRatio { get; set; }

    /// <summary>
    /// Average bolus insulin per bolus (units), excluding basal
    /// </summary>
    public double AveragePerBolus { get; set; }

    /// <summary>
    /// Average carbohydrate per carb intake (grams)
    /// </summary>
    public double AverageCarbsPerEntry { get; set; }

    /// <summary>
    /// Average number of boluses per day over the summary's range
    /// </summary>
    public double DailyBoluses { get; set; }

    /// <summary>
    /// Average carbohydrate per day in grams over the summary's range
    /// </summary>
    public double DailyCarbs { get; set; }
}

public class TreatmentTotals
{
    public FoodTotals Food { get; set; } = new();

    public InsulinTotals Insulin { get; set; } = new();
}

public class FoodTotals
{
    /// <summary>
    /// Total carbohydrates in grams
    /// </summary>
    public double Carbs { get; set; }

    /// <summary>
    /// Total protein in grams
    /// </summary>
    public double Protein { get; set; }

    /// <summary>
    /// Total fat in grams
    /// </summary>
    public double Fat { get; set; }
}

/// <summary>
/// Insulin totals of a <see cref="TreatmentSummary"/>. It summarises bolus records only;
/// basal delivery is reported by <see cref="InsulinDeliveryStatistics"/>.
/// </summary>
public class InsulinTotals
{
    /// <summary>
    /// Total bolus insulin in units
    /// </summary>
    public double Bolus { get; set; }
}

/// <summary>
/// Overall averages across multiple days
/// </summary>
public class OverallAverages
{
    /// <summary>
    /// Average daily bolus insulin
    /// </summary>
    public double AvgBolus { get; set; }

    /// <summary>
    /// Average daily carbohydrates
    /// </summary>
    public double AvgCarbs { get; set; }

    /// <summary>
    /// Average daily protein
    /// </summary>
    public double AvgProtein { get; set; }

    /// <summary>
    /// Average daily fat
    /// </summary>
    public double AvgFat { get; set; }

    /// <summary>
    /// Average time in range percentage
    /// </summary>
    public double AvgTimeInRange { get; set; }

    /// <summary>
    /// Average tight time in range percentage
    /// </summary>
    public double AvgTightTimeInRange { get; set; }
}

/// <summary>
/// Day data containing treatments and metrics
/// </summary>
public class DayData
{
    /// <summary>
    /// Date in ISO format (YYYY-MM-DD)
    /// </summary>
    public string Date { get; set; } = string.Empty;

    public IEnumerable<Treatment> Treatments { get; set; } = Enumerable.Empty<Treatment>();

    public TreatmentSummary TreatmentSummary { get; set; } = new();

    public TimeInRangeMetrics TimeInRanges { get; set; } = new();
}

public class ExtendedAnalysisConfig
{
    public GlycemicThresholds Thresholds { get; set; } = new();

    /// <summary>
    /// Type of continuous glucose monitor sensor
    /// </summary>
    public string SensorType { get; set; } = "GENERIC_5MIN";

    public bool IncludeLoopingMetrics { get; set; } = false;

    /// <summary>
    /// Units for glucose values (mg/dl or mmol/l)
    /// </summary>
    public string Units { get; set; } = "mg/dl";
}

public class DataQuality
{
    /// <summary>
    /// Total expected readings
    /// </summary>
    public int TotalReadings { get; set; }

    public int MissingReadings { get; set; }

    /// <summary>
    /// Percentage of data completeness (0-100)
    /// </summary>
    public double DataCompleteness { get; set; }

    /// <summary>
    /// Percentage of time CGM was active (0-100)
    /// </summary>
    public double CgmActivePercent { get; set; }

    public GapAnalysis GapAnalysis { get; set; } = new();

    /// <summary>
    /// Noise level in the data (0-1, where 0 is no noise)
    /// </summary>
    public double NoiseLevel { get; set; }

    public int CalibrationEvents { get; set; }

    public int SensorWarmups { get; set; }
}

public class GapAnalysis
{
    public IEnumerable<DataGap> Gaps { get; set; } = Enumerable.Empty<DataGap>();

    /// <summary>
    /// Duration of the longest gap in minutes
    /// </summary>
    public double LongestGap { get; set; }

    /// <summary>
    /// Average gap duration in minutes
    /// </summary>
    public double AverageGap { get; set; }
}

public class DataGap
{
    /// <summary>
    /// Start time of the gap (milliseconds since epoch)
    /// </summary>
    public long Start { get; set; }

    /// <summary>
    /// End time of the gap (milliseconds since epoch)
    /// </summary>
    public long End { get; set; }

    /// <summary>
    /// Duration of the gap in minutes
    /// </summary>
    public double Duration { get; set; }
}

public class GlucoseAnalytics
{
    /// <summary>
    /// Basic statistical metrics
    /// </summary>
    public BasicGlucoseStats BasicStats { get; set; } = new();

    public TimeInRangeMetrics TimeInRange { get; set; } = new();

    /// <summary>
    /// Glycemic variability metrics, or null when the window held fewer than two readings. Every
    /// metric has a clinical band whose best end is at or near zero, so a defaulted instance reads
    /// as excellent control rather than as absent data.
    /// </summary>
    public GlycemicVariability? GlycemicVariability { get; set; }

    public DataQuality DataQuality { get; set; } = new();

    /// <summary>
    /// Reliability assessment for this analysis block
    /// </summary>
    public StatisticReliability? Reliability { get; set; }

    public AnalysisTime Time { get; set; } = new();

    /// <summary>
    /// Clinical assessment with insights, strengths, and priority areas
    /// </summary>
    public ClinicalTargetAssessment? ClinicalAssessment { get; set; }

    /// <summary>
    /// Why the tenant's timezone was unavailable. Without a zone, <see cref="GlycemicVariability"/> has no
    /// average daily risk range and <see cref="Reliability"/> counts days of data on UTC days. Null when a zone was applied.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

public class AnalysisTime
{
    /// <summary>
    /// Analysis start time (milliseconds since epoch)
    /// </summary>
    public long Start { get; set; }

    /// <summary>
    /// Analysis end time (milliseconds since epoch)
    /// </summary>
    public long End { get; set; }

    /// <summary>
    /// Time when the analysis was performed (milliseconds since epoch)
    /// </summary>
    public long TimeOfAnalysis { get; set; }
}

/// <summary>
/// Multi-period statistics response containing statistics for different time periods
/// </summary>
public class MultiPeriodStatistics
{
    /// <summary>
    /// Statistics for the last day (24 hours)
    /// </summary>
    public PeriodStatistics LastDay { get; set; } = new();

    /// <summary>
    /// Statistics for the last 3 days (72 hours)
    /// </summary>
    public PeriodStatistics Last3Days { get; set; } = new();

    /// <summary>
    /// Statistics for the last week (7 days)
    /// </summary>
    public PeriodStatistics LastWeek { get; set; } = new();

    /// <summary>
    /// Statistics for the last month (30 days)
    /// </summary>
    public PeriodStatistics LastMonth { get; set; } = new();

    /// <summary>
    /// Statistics for the last 90 days
    /// </summary>
    public PeriodStatistics Last90Days { get; set; } = new();

    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Statistics for a specific time period
/// </summary>
public class PeriodStatistics
{
    /// <summary>
    /// Number of days in the period
    /// </summary>
    public int PeriodDays { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public GlucoseAnalytics? Analytics { get; set; }

    public TreatmentSummary? TreatmentSummary { get; set; }

    public InsulinDeliveryStatistics? InsulinDelivery { get; set; }

    /// <summary>
    /// Indicates if there was sufficient data for meaningful statistics
    /// </summary>
    public bool HasSufficientData { get; set; }

    public GlucoseManagementIndicator? Gmi { get; set; }

    /// <summary>
    /// Reliability assessment for this period's statistics
    /// </summary>
    public StatisticReliability? Reliability { get; set; }

    public int EntryCount { get; set; }

    public int TreatmentCount { get; set; }
}

/// <summary>
/// Averaged statistics for a specific hour of the day
/// </summary>
public class AveragedStats : BasicGlucoseStats
{
    /// <summary>
    /// Hour of the day (0-23)
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Number of distinct local days with at least one reading in this hour
    /// </summary>
    public int DayCount { get; set; }

    /// <summary>
    /// Time in each consensus band for this hour
    /// </summary>
    public ExtendedTimeInRangePercentages TimeInRange { get; set; } = new();
}

/// <summary>
/// Side of the consensus range an hour's out-of-range readings mostly fall on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HourlyExcursion>))]
public enum HourlyExcursion
{
    /// <summary>No reading in the hour was out of range.</summary>
    None,

    /// <summary>More of the hour's readings were below range than above it.</summary>
    Below,

    /// <summary>More of the hour's readings were above range than below it.</summary>
    Above,

    /// <summary>As many of the hour's readings were below range as above it.</summary>
    Mixed,
}

/// <summary>
/// One hour of the day in the hourly-patterns report: its averaged statistics plus how its
/// readings split around the consensus 70-180 mg/dL range.
/// </summary>
public class HourlyPattern : AveragedStats
{
    /// <summary>Percentage of the hour's readings from 70 to 180 mg/dL inclusive.</summary>
    public double InRange { get; set; }

    /// <summary>Percentage of the hour's readings below 70 mg/dL.</summary>
    public double BelowRange { get; set; }

    /// <summary>Percentage of the hour's readings above 180 mg/dL.</summary>
    public double AboveRange { get; set; }

    /// <summary>Side of the range the hour's out-of-range readings mostly fall on.</summary>
    public HourlyExcursion MainExcursion { get; set; }

    /// <summary>
    /// Whether the hour has enough data to be compared with the others. An unranked hour is never
    /// named among the best, worst or most-below-range hours.
    /// </summary>
    public bool IsRanked { get; set; }
}

/// <summary>
/// How far the hourly-patterns report could compare the hours of the day.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HourlyComparison>))]
public enum HourlyComparison
{
    /// <summary>No hour has a reading.</summary>
    NoReadings,

    /// <summary>Fewer than two hours have enough data to rank, so none are compared.</summary>
    TooLittleData,

    /// <summary>
    /// The ranked hours' time in range spans less than <see cref="HourlyPatterns.MinimumSpreadToRank"/>,
    /// so no hour is named best or worst.
    /// </summary>
    CloseTogether,

    /// <summary>
    /// At least one hour is named best or worst. One list may be empty: see
    /// <see cref="HourlyPatterns.BestHours"/>.
    /// </summary>
    Ranked,
}

/// <summary>
/// Which clock the hours of a report were bucketed on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HourlyClockBasis>))]
public enum HourlyClockBasis
{
    /// <summary>The tenant's configured timezone.</summary>
    TenantTimeZone,

    /// <summary>
    /// No timezone resolved, so each reading was placed by the UTC offset its uploader recorded,
    /// or on UTC when it recorded none.
    /// </summary>
    ReadingOffsets,
}

/// <summary>
/// Why no tenant timezone was available. What a report does without one is stated on the response
/// that carries the reason.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimeZoneUnavailableReason>))]
public enum TimeZoneUnavailableReason
{
    /// <summary>The tenant has no timezone set; the owner can set one.</summary>
    NotConfigured,

    /// <summary>The tenant's stored timezone is not one this server recognises; the owner can correct it.</summary>
    Unrecognised,

    /// <summary>The request came through a public share, which cannot read the tenant's settings.</summary>
    Share,

    /// <summary>Looking the timezone up failed.</summary>
    LookupFailed,
}

/// <summary>
/// Which hours of the day go best and worst, bucketed on the tenant's local clock.
/// </summary>
public class HourlyPatterns
{
    /// <summary>How far the hours could be compared.</summary>
    public HourlyComparison Comparison { get; set; }

    /// <summary>All 24 hours in order, midnight first.</summary>
    public List<HourlyPattern> Hours { get; set; } = [];

    /// <summary>
    /// Up to three ranked hours with the most time in range, best first. Empty when no hour at the
    /// top stands out: the top hours tie one another past the list's end, or none is at least
    /// <see cref="MinimumSpreadToRank"/> ahead of every worst hour and the lowest ranked hour. Can
    /// be empty while <see cref="WorstHours"/> is not, and always is unless <see cref="Comparison"/>
    /// is <see cref="HourlyComparison.Ranked"/>.
    /// </summary>
    public List<HourlyPattern> BestHours { get; set; } = [];

    /// <summary>
    /// Up to three ranked hours with the least time in range, worst first, on the same rules as
    /// <see cref="BestHours"/> from the other end. Every one trails every best hour by at least
    /// <see cref="MinimumSpreadToRank"/>, so the two never share an hour. Can be empty while
    /// <see cref="BestHours"/> is not.
    /// </summary>
    public List<HourlyPattern> WorstHours { get; set; } = [];

    /// <summary>
    /// Up to three ranked hours with the most time below range, most first. An hour with readings
    /// below range on fewer than <see cref="MinimumLowDaysToList"/> days is left out.
    /// </summary>
    public List<HourlyPattern> MostBelowRangeHours { get; set; } = [];

    /// <summary>Number of hours with enough data to be ranked.</summary>
    public int RankedHourCount { get; set; }

    /// <summary>Distinct local days an hour needs readings on before it is ranked.</summary>
    public int MinimumDaysToRank { get; set; }

    /// <summary>Readings an hour needs in all before it is ranked.</summary>
    public int MinimumReadingsToRank { get; set; }

    /// <summary>
    /// Percentage points of time in range the best and worst ranked hours must differ by before
    /// any hour is named best or worst.
    /// </summary>
    public double MinimumSpreadToRank { get; set; }

    /// <summary>Distinct local days an hour needs readings below range on to be listed as most below range.</summary>
    public int MinimumLowDaysToList { get; set; }

    /// <summary>The band edges the hours were classified on.</summary>
    public GlycemicThresholds Thresholds { get; set; } = new();

    /// <summary>Which clock the hours were bucketed on.</summary>
    public HourlyClockBasis ClockBasis { get; set; }

    /// <summary>
    /// Identifier of the timezone the hours were bucketed on; null when <see cref="ClockBasis"/> is
    /// <see cref="HourlyClockBasis.ReadingOffsets"/>.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Why <see cref="ClockBasis"/> is <see cref="HourlyClockBasis.ReadingOffsets"/>; null when the
    /// hours followed the tenant's timezone.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// Mean glucose per weekday in one five-minute slot of the day, for the week-to-week report.
/// Slots are keyed on the tenant's local clock, so a reading is bucketed on the weekday and
/// time of day the patient experienced it.
/// </summary>
public class WeekdayGlucoseSlot
{
    /// <summary>Minutes after local midnight at which the slot starts (0, 5, ..., 1435).</summary>
    public int MinuteOfDay { get; set; }

    /// <summary>
    /// Mean glucose (mg/dL) of the readings in this slot on each weekday. A weekday with no
    /// readings in the slot is absent.
    /// </summary>
    public Dictionary<DayOfWeek, double> Mean { get; set; } = new();
}

/// <summary>
/// Response for <c>GET /api/v4/Statistics/weekday-averages</c>.
/// </summary>
public class WeekdayAveragesResponse
{
    /// <summary>The populated slots in time-of-day order.</summary>
    public List<WeekdayGlucoseSlot> Slots { get; set; } = [];

    /// <summary>Why <see cref="Slots"/> is empty; null when the tenant's timezone resolved.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// Diabetes population types for clinical target assessment
/// </summary>
public enum DiabetesPopulation
{
    /// <summary>Type 1 diabetes adult</summary>
    Type1Adult,

    /// <summary>Type 2 diabetes adult</summary>
    Type2Adult,

    /// <summary>Type 1 diabetes pediatric (under 18)</summary>
    Type1Pediatric,

    /// <summary>Elderly or high-risk individuals (over 65 or significant comorbidities)</summary>
    Elderly,

    /// <summary>Pregnancy with gestational diabetes</summary>
    Pregnancy,

    /// <summary>Pregnancy with pre-existing Type 1 diabetes</summary>
    PregnancyType1,
}

/// <summary>
/// Clinical targets based on international consensus (2019 TIR Consensus, 2023 GRI)
/// </summary>
public class ClinicalTargets
{
    /// <summary>Target time in range percentage (70-180 mg/dL)</summary>
    public double TargetTIR { get; set; }

    /// <summary>Maximum time below range percentage (&lt;70 mg/dL)</summary>
    public double MaxTBR { get; set; }

    /// <summary>Maximum time very low percentage (&lt;54 mg/dL)</summary>
    public double MaxTBRVeryLow { get; set; }

    /// <summary>Maximum time above range percentage (&gt;180 mg/dL)</summary>
    public double MaxTAR { get; set; }

    /// <summary>Maximum time very high percentage (&gt;250 mg/dL)</summary>
    public double MaxTARVeryHigh { get; set; }

    /// <summary>Target coefficient of variation percentage</summary>
    public double TargetCV { get; set; }

    /// <summary>Target range lower bound (mg/dL)</summary>
    public double TargetLow { get; set; }

    /// <summary>Target range upper bound (mg/dL)</summary>
    public double TargetHigh { get; set; }

    /// <summary>
    /// Returns the evidence-based clinical targets for the specified <see cref="DiabetesPopulation"/>.
    /// Based on International Consensus on Time in Range (2019) and subsequent updates.
    /// </summary>
    /// <returns>A <see cref="ClinicalTargets"/> instance with population-appropriate thresholds</returns>
    public static ClinicalTargets ForPopulation(DiabetesPopulation population)
    {
        return population switch
        {
            DiabetesPopulation.Type1Adult or DiabetesPopulation.Type2Adult => new ClinicalTargets
            {
                TargetTIR = 70,
                MaxTBR = 4,
                MaxTBRVeryLow = 1,
                MaxTAR = 25,
                MaxTARVeryHigh = 5,
                TargetCV = GlucoseConstants.CoefficientOfVariationTargetPercent,
                TargetLow = GlucoseConstants.TargetBottomMgdl,
                TargetHigh = GlucoseConstants.TargetTopMgdl,
            },
            DiabetesPopulation.Type1Pediatric => new ClinicalTargets
            {
                TargetTIR = 70,
                MaxTBR = 4,
                MaxTBRVeryLow = 1,
                MaxTAR = 25,
                MaxTARVeryHigh = 5,
                TargetCV = GlucoseConstants.CoefficientOfVariationTargetPercent,
                TargetLow = GlucoseConstants.TargetBottomMgdl,
                TargetHigh = GlucoseConstants.TargetTopMgdl,
            },
            DiabetesPopulation.Elderly => new ClinicalTargets
            {
                TargetTIR = 50,
                MaxTBR = 1,
                MaxTBRVeryLow = 0,
                MaxTAR = 50,
                MaxTARVeryHigh = 10,
                TargetCV = GlucoseConstants.CoefficientOfVariationTargetPercent,
                TargetLow = GlucoseConstants.TargetBottomMgdl,
                TargetHigh = GlucoseConstants.TargetTopMgdl,
            },
            DiabetesPopulation.Pregnancy => new ClinicalTargets
            {
                TargetTIR = 70,
                MaxTBR = 4,
                MaxTBRVeryLow = 1,
                MaxTAR = 25,
                MaxTARVeryHigh = 5,
                TargetCV = GlucoseConstants.CoefficientOfVariationTargetPercent,
                TargetLow = 63,
                TargetHigh = 140,
            },
            DiabetesPopulation.PregnancyType1 => new ClinicalTargets
            {
                TargetTIR = 70,
                MaxTBR = 4,
                MaxTBRVeryLow = 1,
                MaxTAR = 25,
                MaxTARVeryHigh = 5,
                TargetCV = GlucoseConstants.CoefficientOfVariationTargetPercent,
                TargetLow = 63,
                TargetHigh = 140,
            },
            _ => ForPopulation(DiabetesPopulation.Type1Adult),
        };
    }
}

/// <summary>
/// Glucose Management Indicator (GMI) - modern replacement for estimated A1c
/// Based on: GMI (%) = 3.31 + (0.02392 × mean glucose in mg/dL)
/// </summary>
public class GlucoseManagementIndicator
{
    /// <summary>GMI value as percentage (e.g., 7.0 for 7.0%)</summary>
    public double Value { get; set; }

    /// <summary>Mean glucose used for calculation (mg/dL)</summary>
    public double MeanGlucose { get; set; }

    public GlucoseManagementIndicatorLevel Interpretation { get; set; }

    /// <summary>Reliability assessment for this GMI calculation</summary>
    public StatisticReliability? Reliability { get; set; }

    /// <summary>
    /// Returns the <see cref="GlucoseManagementIndicatorLevel"/> category for a given GMI value
    /// based on ADA Standards of Care.
    /// </summary>
    /// <param name="gmi">GMI percentage value (e.g., 7.0 for 7.0%)</param>
    /// <returns>The corresponding <see cref="GlucoseManagementIndicatorLevel"/></returns>
    public static GlucoseManagementIndicatorLevel GetInterpretation(double gmi)
    {
        return gmi switch
        {
            < 5.7 => GlucoseManagementIndicatorLevel.NonDiabetic,
            < 6.5 => GlucoseManagementIndicatorLevel.Prediabetes,
            < 7.0 => GlucoseManagementIndicatorLevel.WellControlled,
            < 8.0 => GlucoseManagementIndicatorLevel.ModerateControl,
            < 9.0 => GlucoseManagementIndicatorLevel.SuboptimalControl,
            _ => GlucoseManagementIndicatorLevel.PoorControl,
        };
    }
}

/// <summary>
/// Glycemic Risk Index (GRI) - composite risk score from 0-100
/// Based on 2023 International Consensus
/// Hypo component = VLow% + (0.8 × Low%), Hyper component = VHigh% + (0.5 × High%)
/// GRI = (3.0 × Hypo) + (1.6 × Hyper)
/// </summary>
public class GlycemicRiskIndex
{
    /// <summary>Overall GRI score (0-100, lower is better)</summary>
    public double Score { get; set; }

    /// <summary>Hypo component = VLow% + (0.8 × Low%)</summary>
    public double HypoglycemiaComponent { get; set; }

    /// <summary>Hyper component = VHigh% + (0.5 × High%)</summary>
    public double HyperglycemiaComponent { get; set; }

    public GRIZone Zone { get; set; }

    /// <summary>Interpretation of the GRI score</summary>
    public GlycomicRiskInterpretation Interpretation { get; set; }
}

/// <summary>
/// GRI Zone classifications based on the GRI grid
/// </summary>
public enum GRIZone
{
    /// <summary>Zone A: Lowest risk (GRI 0-20)</summary>
    A,

    /// <summary>Zone B: Low risk (GRI 21-40)</summary>
    B,

    /// <summary>Zone C: Moderate risk (GRI 41-60)</summary>
    C,

    /// <summary>Zone D: High risk (GRI 61-80)</summary>
    D,

    /// <summary>Zone E: Very high risk (GRI 81-100)</summary>
    E,
}

/// <summary>
/// Period-specific glucose metrics for time-of-day analysis
/// </summary>
public class PeriodMetrics
{
    /// <summary>Name of the period (e.g., "Overnight", "Morning")</summary>
    public string PeriodName { get; set; } = string.Empty;

    public int ReadingCount { get; set; }

    /// <summary>Mean glucose (mg/dL)</summary>
    public double Mean { get; set; }

    /// <summary>Median glucose (mg/dL)</summary>
    public double Median { get; set; }

    /// <summary>Standard deviation (mg/dL)</summary>
    public double StandardDeviation { get; set; }

    /// <summary>Time in range percentage (%)</summary>
    public double TimeInRange { get; set; }

    /// <summary>Minimum glucose value (mg/dL)</summary>
    public double Min { get; set; }

    /// <summary>Maximum glucose value (mg/dL)</summary>
    public double Max { get; set; }
}

/// <summary>
/// Reliability metadata for a statistics analysis block.
/// Provides raw facts so the frontend can compose a plain-English message
/// when the data doesn't meet clinical reliability criteria.
/// </summary>
public class StatisticReliability
{
    /// <summary>Whether the data meets clinical reliability criteria for this analysis</summary>
    public bool MeetsReliabilityCriteria { get; set; }

    /// <summary>Number of days with glucose data in this analysis window, counted on UTC days when the tenant has no timezone</summary>
    public int DaysOfData { get; set; }

    /// <summary>Minimum days recommended by clinical guidelines for reliable results</summary>
    public int RecommendedMinimumDays { get; set; }

    /// <summary>Number of glucose readings used in this analysis</summary>
    public int ReadingCount { get; set; }
}

public enum TargetStatus
{
    Met,

    /// <summary>Close to target (within 10%)</summary>
    Close,

    NotMet,

    /// <summary>
    /// The metric could not be computed from the data, so the target is neither met nor missed
    /// </summary>
    NotAssessed,
}

public class TargetAssessment
{
    public string MetricName { get; set; } = string.Empty;

    /// <summary>Current value, or null when <see cref="Status"/> is <see cref="TargetStatus.NotAssessed"/></summary>
    public double? CurrentValue { get; set; }

    public double TargetValue { get; set; }

    /// <summary>Whether target is a maximum (true) or minimum (false)</summary>
    public bool IsMaximumTarget { get; set; }

    public TargetStatus Status { get; set; }

    /// <summary>Difference from target, or null when not assessed</summary>
    public double? DifferenceFromTarget { get; set; }

    /// <summary>Percentage progress toward target, or null when not assessed</summary>
    public double? ProgressPercentage { get; set; }
}

/// <summary>
/// Localized insight with key and context data for frontend formatting
/// </summary>
public class LocalizedInsight
{
    /// <summary>The insight key for frontend localization</summary>
    public InsightKey Key { get; set; }

    /// <summary>Context data for message formatting (e.g., actual values, targets)</summary>
    public Dictionary<string, double> Context { get; set; } = new();
}

public class ClinicalTargetAssessment
{
    public DiabetesPopulation Population { get; set; }

    public ClinicalTargets Targets { get; set; } = new();

    /// <summary>Time in range assessment</summary>
    public TargetAssessment TIRAssessment { get; set; } = new();

    /// <summary>Time below range assessment</summary>
    public TargetAssessment TBRAssessment { get; set; } = new();

    public TargetAssessment VeryLowAssessment { get; set; } = new();

    /// <summary>Time above range assessment</summary>
    public TargetAssessment TARAssessment { get; set; } = new();

    public TargetAssessment VeryHighAssessment { get; set; } = new();

    /// <summary>Coefficient of variation assessment</summary>
    public TargetAssessment CVAssessment { get; set; } = new();

    public int TargetsMet { get; set; }

    /// <summary>Total number of targets, assessed or not</summary>
    public int TotalTargets { get; set; }

    /// <summary>
    /// Number of targets whose metric could be computed; the rest are
    /// <see cref="TargetStatus.NotAssessed"/>
    /// </summary>
    public int TargetsAssessed { get; set; }

    public ClinicalAssessmentLevel OverallAssessment { get; set; }

    /// <summary>List of actionable insights/recommendations with localization keys and context</summary>
    public List<LocalizedInsight> ActionableInsights { get; set; } = new();

    /// <summary>Priority areas for improvement with localization keys and context</summary>
    public List<LocalizedInsight> PriorityAreas { get; set; } = new();

    /// <summary>Strengths/achievements to acknowledge with localization keys and context</summary>
    public List<LocalizedInsight> Strengths { get; set; } = new();
}

public class DataSufficiencyAssessment
{
    /// <summary>Whether there is sufficient data for a valid report</summary>
    public bool IsSufficient { get; set; }

    /// <summary>Number of days in the period</summary>
    public int TotalDays { get; set; }

    /// <summary>Number of days with any data, counted on UTC days when <see cref="TimeZoneUnavailableReason"/> is set.</summary>
    public int DaysWithData { get; set; }

    /// <summary>Expected number of readings (based on sensor type)</summary>
    public int ExpectedReadings { get; set; }

    public int ActualReadings { get; set; }

    /// <summary>Data completeness percentage</summary>
    public double CompletenessPercentage { get; set; }

    /// <summary>Minimum required completeness for valid report (typically 70%)</summary>
    public double MinimumRequiredCompleteness { get; set; } = 70;

    /// <summary>Average readings per day, over the days of <see cref="DaysWithData"/></summary>
    public double AverageReadingsPerDay { get; set; }

    /// <summary>Longest data gap in hours</summary>
    public double LongestGapHours { get; set; }

    /// <summary>Data sufficiency status (use this for localization on frontend)</summary>
    public DataSufficiencyStatus Status { get; set; } = DataSufficiencyStatus.Sufficient;

    /// <summary>Warning message if data is insufficient</summary>
    public string WarningMessage { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    /// <summary>Why <see cref="DaysWithData"/> and <see cref="AverageReadingsPerDay"/> are counted on UTC days; null when a zone was applied.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

public class ExtendedGlucoseAnalytics : GlucoseAnalytics
{
    /// <summary>Glucose Management Indicator (modern replacement for eA1c)</summary>
    public GlucoseManagementIndicator GMI { get; set; } = new();

    /// <summary>Glycemic Risk Index (composite risk score)</summary>
    public GlycemicRiskIndex GRI { get; set; } = new();

    public new ClinicalTargetAssessment ClinicalAssessment { get; set; } = new();

    public DataSufficiencyAssessment DataSufficiency { get; set; } = new();

    /// <summary>Treatment summary if treatment data is available</summary>
    public TreatmentSummary? TreatmentSummary { get; set; }
}

/// <summary>
/// Bundled result of the server-side range-analytics endpoint: extended glucose
/// analytics plus time-of-day averaged stats for the requested window.
/// </summary>
/// <seealso cref="ExtendedGlucoseAnalytics"/>
/// <seealso cref="AveragedStats"/>
public class ReportAnalysisResult
{
    /// <summary>Extended glucose analytics (TIR, GMI, GRI, variability, clinical assessment, etc.).</summary>
    public ExtendedGlucoseAnalytics Analysis { get; set; } = new();

    /// <summary>Time-of-day averaged statistics for AGP-style charts.</summary>
    public IEnumerable<AveragedStats> AveragedStats { get; set; } = [];

    /// <summary>The band edges <see cref="AveragedStats"/> partitions each hour's readings on.</summary>
    public GlycemicThresholds HourlyBandThresholds { get; set; } = new();

    /// <summary>Registered devices that contributed readings within the requested window, for device-picker UIs.</summary>
    public List<ContributingDevice> ContributingDevices { get; set; } = new();

    /// <summary>
    /// Time in the tenant's personal target range schedule, alongside the clinical-consensus
    /// TIR in <see cref="Analysis"/>. Null when the tenant has no target range schedule
    /// configured, the window has no readings, or the tenant has no usable timezone and the schedule
    /// has several entries.
    /// </summary>
    public PersonalRangeTimeInRange? PersonalRange { get; set; }

    /// <summary>
    /// Why the tenant's timezone was unavailable. Without a zone, <see cref="AveragedStats"/> groups
    /// readings by their own UTC offset, <see cref="Analysis"/> has no average daily risk range and
    /// counts days of data on UTC days, and <see cref="PersonalRange"/> is null unless the target
    /// schedule has one entry. Null when a zone was applied.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// Time spent below / within / above a personal target range schedule. A personal range is a
/// below/within/above split, not the 6-band clinical breakdown — the schedule's Low/High are the
/// only boundaries. Evaluated per reading against the schedule entry active at that reading's
/// tenant-local time of day.
/// </summary>
public class PersonalRangeTimeInRange
{
    /// <summary>Percentage of readings below the active entry's Low bound.</summary>
    public double BelowRangePercent { get; set; }

    /// <summary>Percentage of readings within the active entry's [Low, High] bounds.</summary>
    public double InRangePercent { get; set; }

    /// <summary>Percentage of readings above the active entry's High bound.</summary>
    public double AboveRangePercent { get; set; }

    /// <summary>
    /// The schedule entries the percentages were computed against, so callers can label the
    /// result (e.g. "Your range: 80-160") without a second call.
    /// </summary>
    public List<TargetRangeEntry> Entries { get; set; } = [];
}

/// <summary>
/// A registered device (or the unattributed pseudo-device) that contributed CGM readings
/// within a statistics computation window.
/// </summary>
public sealed class ContributingDevice
{
    /// <summary>The registered <see cref="PatientDevice"/> id, or <c>null</c> for the unattributed bucket.</summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>Display name of the device, or <c>null</c> for the unattributed bucket.</summary>
    public string? Name { get; set; }

    /// <summary>Number of readings attributed to this device within the requested window.</summary>
    public int ReadingCount { get; set; }
}

/// <summary>
/// Data point for site change impact analysis
/// Averaged glucose at a specific time offset from site change
/// </summary>
public class SiteChangeImpactDataPoint
{
    /// <summary>Minutes from site change (negative = before, positive = after)</summary>
    public int MinutesFromChange { get; set; }

    /// <summary>Average glucose at this time offset (mg/dL)</summary>
    public double AverageGlucose { get; set; }

    /// <summary>Median glucose at this time offset (mg/dL)</summary>
    public double MedianGlucose { get; set; }

    /// <summary>Standard deviation of glucose values</summary>
    public double StdDev { get; set; }

    /// <summary>Number of readings contributing to this data point</summary>
    public int Count { get; set; }

    /// <summary>10th percentile glucose value (mg/dL)</summary>
    public double Percentile10 { get; set; }

    /// <summary>25th percentile glucose value (mg/dL)</summary>
    public double Percentile25 { get; set; }

    /// <summary>75th percentile glucose value (mg/dL)</summary>
    public double Percentile75 { get; set; }

    /// <summary>90th percentile glucose value (mg/dL)</summary>
    public double Percentile90 { get; set; }
}

public class SiteChangeImpactSummary
{
    /// <summary>Average glucose before site change (mg/dL)</summary>
    public double AvgGlucoseBeforeChange { get; set; }

    /// <summary>Average glucose after site change (mg/dL)</summary>
    public double AvgGlucoseAfterChange { get; set; }

    /// <summary>Time in range before site change (%)</summary>
    public double TimeInRangeBeforeChange { get; set; }

    /// <summary>Time in range after site change (%)</summary>
    public double TimeInRangeAfterChange { get; set; }

    /// <summary>Coefficient of variation before site change (%)</summary>
    public double CvBeforeChange { get; set; }

    /// <summary>Coefficient of variation after site change (%)</summary>
    public double CvAfterChange { get; set; }
}

/// <summary>
/// Comprehensive insulin delivery statistics for reports
/// </summary>
public class InsulinDeliveryStatistics
{
    /// <summary>
    /// Total bolus insulin in units for the period: <see cref="ManualBolus"/> plus
    /// <see cref="AutomaticBolus"/>.
    /// </summary>
    public double TotalBolus { get; set; }

    /// <summary>
    /// Insulin in units from user-initiated boluses for the period.
    /// </summary>
    public double ManualBolus { get; set; }

    /// <summary>
    /// Insulin in units from algorithm-delivered boluses (SMBs) for the period.
    /// </summary>
    public double AutomaticBolus { get; set; }

    /// <summary>
    /// Total basal insulin in units for the period: temp basal delivery plus long-acting basal
    /// injections. Algorithm boluses are bolus insulin and are not included.
    /// </summary>
    public double TotalBasal { get; set; }

    /// <summary>
    /// Scheduled (profile) basal insulin in units for the period
    /// </summary>
    public double ScheduledBasal { get; set; }

    /// <summary>
    /// Temp basal insulin above the scheduled rate (temp - scheduled) for the period
    /// </summary>
    public double AdditionalBasal { get; set; }

    /// <summary>
    /// Total insulin for the period: <see cref="TotalBolus"/> plus <see cref="TotalBasal"/>
    /// </summary>
    public double TotalInsulin { get; set; }

    /// <summary>
    /// Total carbohydrates consumed in grams
    /// </summary>
    public double TotalCarbs { get; set; }

    /// <summary>
    /// Grams of carbohydrate per day: <see cref="TotalCarbs"/> over <see cref="WindowDays"/>
    /// </summary>
    public double CarbsPerDay { get; set; }

    /// <summary>
    /// Number of manually initiated boluses in the period. Algorithm-delivered boluses
    /// are excluded and counted by <see cref="AutomaticBolusCount"/>.
    /// </summary>
    public int ManualBolusCount { get; set; }

    /// <summary>
    /// Number of basal delivery records in the period: TempBasal segments plus discrete
    /// long-acting basal injections.
    /// </summary>
    public int BasalCount { get; set; }

    /// <summary>
    /// Total insulin events in the period: manual boluses, automatic boluses, and basal deliveries.
    /// </summary>
    public int InsulinEventCount { get; set; }

    /// <summary>
    /// <see cref="TotalBasal"/> as a percentage of <see cref="TotalInsulin"/> (0-100)
    /// </summary>
    public double BasalPercent { get; set; }

    /// <summary>
    /// <see cref="TotalBolus"/>, manual and automatic, as a percentage of <see cref="TotalInsulin"/> (0-100)
    /// </summary>
    public double BolusPercent { get; set; }

    /// <summary>
    /// Average Total Daily Dose (units per day): <see cref="TotalInsulin"/> over <see cref="WindowDays"/>
    /// </summary>
    public double Tdd { get; set; }

    /// <summary>
    /// Units per day of <see cref="ManualBolus"/>, over <see cref="WindowDays"/>
    /// </summary>
    public double ManualBolusPerDay { get; set; }

    /// <summary>
    /// Units per day of <see cref="AutomaticBolus"/>, over <see cref="WindowDays"/>
    /// </summary>
    public double AutomaticBolusPerDay { get; set; }

    /// <summary>
    /// Units per day of <see cref="TotalBasal"/>, over <see cref="WindowDays"/>
    /// </summary>
    public double TotalBasalPerDay { get; set; }

    /// <summary>
    /// Units per day of <see cref="TotalBolus"/>, over <see cref="WindowDays"/>
    /// </summary>
    public double TotalBolusPerDay { get; set; }

    /// <summary>
    /// Units per day of <see cref="ScheduledBasal"/>, over <see cref="WindowDays"/>
    /// </summary>
    public double ScheduledBasalPerDay { get; set; }

    /// <summary>
    /// Average manual bolus size (units): <see cref="ManualBolus"/> over <see cref="ManualBolusCount"/>
    /// </summary>
    public double AvgBolus { get; set; }

    /// <summary>
    /// Number of manual boluses paired with a meal: the meal of a carb entry linked to the bolus, else
    /// the nearest meal from 30 minutes before it to 1 hour after it
    /// </summary>
    public int MealBoluses { get; set; }

    /// <summary>
    /// Number of manual boluses paired with no meal
    /// </summary>
    public int CorrectionBoluses { get; set; }

    /// <summary>
    /// <see cref="MealBoluses"/> per day, over <see cref="WindowDays"/>
    /// </summary>
    public double MealBolusesPerDay { get; set; }

    /// <summary>
    /// <see cref="CorrectionBoluses"/> per day, over <see cref="WindowDays"/>
    /// </summary>
    public double CorrectionBolusesPerDay { get; set; }

    /// <summary>
    /// <see cref="MealBoluses"/> as a percentage (0-100) of <see cref="ManualBolusCount"/>; 0 with no manual bolus
    /// </summary>
    public double MealBolusPercent { get; set; }

    /// <summary>
    /// <see cref="CorrectionBoluses"/> as a percentage (0-100) of <see cref="ManualBolusCount"/>; 0 with no manual bolus
    /// </summary>
    public double CorrectionBolusPercent { get; set; }

    /// <summary>
    /// Insulin-to-carb ratio as dosed: grams of the meals a manual bolus is paired with, per unit of
    /// every bolus, manual or automatic, paired with those meals; null when no manual bolus is paired
    /// with a meal
    /// </summary>
    public double? IcRatio { get; set; }

    /// <summary>
    /// Grams of the meals no manual bolus is paired with, such as hypo treatments and unbolused
    /// snacks, which <see cref="IcRatio"/> leaves out
    /// </summary>
    public double UnpairedCarbs { get; set; }

    /// <summary>
    /// Average number of manual boluses per day
    /// </summary>
    public double BolusesPerDay { get; set; }

    /// <summary>
    /// Whole days in the requested window, rounded to nearest, at least one; the per-day
    /// averages over the window divide by it. Not the number of days that hold data.
    /// </summary>
    public int WindowDays { get; set; }

    /// <summary>
    /// Start date of the analysis period (ISO format)
    /// </summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    /// End date of the analysis period (ISO format)
    /// </summary>
    public string EndDate { get; set; } = string.Empty;

    /// <summary>
    /// Number of carb entries (meals with carbs logged)
    /// </summary>
    public int CarbCount { get; set; }

    /// <summary>
    /// Number of treatments with both carbs and bolus
    /// </summary>
    public int CarbBolusCount { get; set; }

    /// <summary>
    /// Number of algorithm-delivered boluses (SMBs) in the period
    /// </summary>
    public int AutomaticBolusCount { get; set; }

    /// <summary>
    /// Total insulin from discrete long-acting basal injections (MDI) in units.
    /// Included in <see cref="TotalBasal"/> and <see cref="ScheduledBasal"/>.
    /// </summary>
    public double BasalInjectionInsulin { get; set; }

    /// <summary>
    /// Number of discrete long-acting basal injections (MDI) in the period.
    /// </summary>
    public int BasalInjectionCount { get; set; }

    /// <summary>
    /// The manual boluses that make up <see cref="ManualBolus"/>, one record per counted bolus.
    /// Filled only when the caller asks for them and holds treatments.read; null otherwise.
    /// </summary>
    public List<Bolus>? ManualBoluses { get; set; }

    public StatisticReliability? Reliability { get; set; }
}

/// <summary>
/// Daily basal/bolus ratio data for chart rendering
/// </summary>
public class DailyBasalBolusRatioData
{
    /// <summary>
    /// Date in ISO format (YYYY-MM-DD)
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Display-formatted date (e.g., "Jan 15")
    /// </summary>
    public string DisplayDate { get; set; } = string.Empty;

    /// <summary>
    /// Basal insulin delivered in units: temp basal and long-acting injections, no algorithm boluses
    /// </summary>
    public double Basal { get; set; }

    /// <summary>
    /// Bolus insulin delivered in units: <see cref="ManualBolus"/> plus <see cref="AutomaticBolus"/>
    /// </summary>
    public double Bolus { get; set; }

    /// <summary>
    /// Insulin from user-initiated boluses in units
    /// </summary>
    public double ManualBolus { get; set; }

    /// <summary>
    /// Insulin from algorithm-delivered boluses (SMBs) in units
    /// </summary>
    public double AutomaticBolus { get; set; }

    /// <summary>
    /// Total insulin (basal + bolus) in units
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// Basal as percentage of total (0-100)
    /// </summary>
    public double BasalPercent { get; set; }

    /// <summary>
    /// Bolus, manual and automatic, as percentage of total (0-100)
    /// </summary>
    public double BolusPercent { get; set; }

    /// <summary>
    /// Carbohydrates logged that day in grams
    /// </summary>
    public double Carbs { get; set; }
}

public class DailyBasalBolusRatioResponse
{
    public List<DailyBasalBolusRatioData> DailyData { get; set; } = new();

    /// <summary>
    /// Average basal percentage across all days (0-100)
    /// </summary>
    public double AverageBasalPercent { get; set; }

    /// <summary>
    /// Average bolus percentage across all days (0-100)
    /// </summary>
    public double AverageBolusPercent { get; set; }

    /// <summary>
    /// Average total daily dose (TDD) in units
    /// </summary>
    public double AverageTdd { get; set; }

    /// <summary>
    /// Distinct local days this response attributes delivered insulin to; its averages divide
    /// by it. Not the length of the requested window.
    /// </summary>
    public int DaysWithData { get; set; }

    /// <summary>Why <see cref="DailyData"/> is empty; null when the tenant's timezone resolved.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// Hourly basal rate percentile data for AGP-style charts
/// </summary>
public class HourlyBasalPercentileData
{
    /// <summary>
    /// Hour of day (0-23)
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// 10th percentile of basal rates at this hour
    /// </summary>
    public double P10 { get; set; }

    /// <summary>
    /// 25th percentile of basal rates at this hour
    /// </summary>
    public double P25 { get; set; }

    /// <summary>
    /// Median (50th percentile) basal rate at this hour
    /// </summary>
    public double Median { get; set; }

    /// <summary>
    /// 75th percentile of basal rates at this hour
    /// </summary>
    public double P75 { get; set; }

    /// <summary>
    /// 90th percentile of basal rates at this hour
    /// </summary>
    public double P90 { get; set; }

    /// <summary>
    /// Number of data points used for this hour
    /// </summary>
    public int Count { get; set; }
}

public class TempBasalInfo
{
    public int Total { get; set; }

    /// <summary>
    /// Average temp basals per day
    /// </summary>
    public double PerDay { get; set; }

    /// <summary>
    /// Number of temp basals with rate higher than scheduled
    /// </summary>
    public int HighTemps { get; set; }

    /// <summary>
    /// Number of temp basals with rate lower than scheduled
    /// </summary>
    public int LowTemps { get; set; }

    /// <summary>
    /// Number of zero or suspend temp basals
    /// </summary>
    public int ZeroTemps { get; set; }
}

/// <summary>
/// Basic basal statistics for a period
/// </summary>
public class BasalStats
{
    public int Count { get; set; }

    /// <summary>
    /// Time-weighted average basal rate in U/hr
    /// </summary>
    public double AvgRate { get; set; }

    /// <summary>
    /// Minimum basal rate in U/hr
    /// </summary>
    public double MinRate { get; set; }

    /// <summary>
    /// Maximum basal rate in U/hr
    /// </summary>
    public double MaxRate { get; set; }

    /// <summary>
    /// Total basal insulin delivered in units, from temp basal records only
    /// </summary>
    public double TotalDelivered { get; set; }

    /// <summary>
    /// <see cref="TotalDelivered"/> per day, over <see cref="BasalAnalysisResponse.WindowDays"/>
    /// </summary>
    public double TotalDeliveredPerDay { get; set; }
}

public class BasalAnalysisResponse
{
    /// <summary>
    /// Basic basal statistics
    /// </summary>
    public BasalStats Stats { get; set; } = new();

    public TempBasalInfo TempBasalInfo { get; set; } = new();

    /// <summary>
    /// Hourly basal rate percentiles for chart rendering
    /// </summary>
    public List<HourlyBasalPercentileData> HourlyPercentiles { get; set; } = new();

    /// <summary>
    /// Whole days in the requested window, rounded to nearest, at least one; the per-day
    /// averages over the window divide by it. Not the number of days that hold data.
    /// </summary>
    public int WindowDays { get; set; }

    public string StartDate { get; set; } = string.Empty;

    public string EndDate { get; set; } = string.Empty;

    /// <summary>
    /// Why <see cref="HourlyPercentiles"/> is empty; null when the tenant's timezone resolved. The
    /// other figures do not depend on the local clock and are served either way.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// Average insulin delivered during one hour of the day, split by delivery kind.
/// Values are units per day, averaged over the days that have delivery data.
/// </summary>
public class HourlyInsulinDeliveryPoint
{
    /// <summary>
    /// Hour of day (0-23) in the user's profile timezone
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Average insulin from scheduled-rate basal delivery (Scheduled/Inferred origin)
    /// </summary>
    public double ScheduledBasal { get; set; }

    /// <summary>
    /// Average insulin from temp adjustments (Algorithm/Manual/Suspended origin)
    /// </summary>
    public double TempBasal { get; set; }

    /// <summary>
    /// Average total basal insulin (scheduled + temp)
    /// </summary>
    public double Basal { get; set; }

    /// <summary>
    /// Average insulin from user-initiated boluses
    /// </summary>
    public double ManualBolus { get; set; }

    /// <summary>
    /// Average number of user-initiated boluses given in this hour per day, over the same days as
    /// <see cref="ManualBolus"/>
    /// </summary>
    public double ManualBolusesPerDay { get; set; }

    /// <summary>
    /// Average insulin from algorithm-delivered boluses (SMBs)
    /// </summary>
    public double AutomaticBolus { get; set; }

    /// <summary>
    /// Average total insulin delivered during this hour: basal plus manual and automatic bolus
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// Number of delivery records contributing to this hour
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Rate (U/h) the profile schedules for this hour, averaged over the requested range weighted by
    /// the time each schedule was in effect. Independent of delivery: a zero temp basal leaves it
    /// unchanged. <see cref="ScheduledBasal"/> is the scheduled-origin insulin actually delivered.
    /// </summary>
    public double ProfileBasalRate { get; set; }
}

/// <summary>
/// One of the three parts the day is split into for <see cref="HourlyInsulinDeliveryResponse"/>:
/// morning (06:00 to 12:00), afternoon (12:00 to 18:00) and evening/night (18:00 to 06:00 the next
/// morning), in the user's profile timezone.
/// </summary>
public class InsulinDeliveryPeriodTotal
{
    /// <summary>
    /// Units over the period's hours, summed from the hourly averages
    /// </summary>
    public double Units { get; set; }

    /// <summary>
    /// Percentage (0-100) of the daily total
    /// </summary>
    public double Percent { get; set; }
}

/// <summary>
/// The three day periods and their sum, for one measure of delivery
/// </summary>
public class InsulinDeliveryPeriodBreakdown
{
    public InsulinDeliveryPeriodTotal Morning { get; set; } = new();

    public InsulinDeliveryPeriodTotal Afternoon { get; set; } = new();

    public InsulinDeliveryPeriodTotal EveningNight { get; set; } = new();

    /// <summary>
    /// Units over all 24 hours
    /// </summary>
    public double DailyTotal { get; set; }
}

/// <summary>
/// Hourly insulin delivery pattern for a period, computed from pump-confirmed
/// delivery records (TempBasals + boluses) only
/// </summary>
public class HourlyInsulinDeliveryResponse
{
    /// <summary>
    /// One entry per hour of day (0-23)
    /// </summary>
    public List<HourlyInsulinDeliveryPoint> Hours { get; set; } = new();

    /// <summary>
    /// Periods over <see cref="HourlyInsulinDeliveryPoint.Total"/>, what the stacked chart plots
    /// </summary>
    public InsulinDeliveryPeriodBreakdown TotalDeliveryPeriods { get; set; } = new();

    /// <summary>
    /// Periods over <see cref="HourlyInsulinDeliveryPoint.Basal"/>, what the basal-only chart plots
    /// </summary>
    public InsulinDeliveryPeriodBreakdown BasalPeriods { get; set; } = new();

    /// <summary>
    /// Distinct local days this response attributes delivered insulin to. Not the length of the
    /// requested window.
    /// </summary>
    public int DaysWithData { get; set; }

    /// <summary>
    /// Start date of the analysis period
    /// </summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    /// End date of the analysis period
    /// </summary>
    public string EndDate { get; set; } = string.Empty;

    /// <summary>Why <see cref="Hours"/> is empty; null when the tenant's timezone resolved.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

public class SiteChangeImpactAnalysis
{
    public int SiteChangeCount { get; set; }

    /// <summary>Average number of days between site changes</summary>
    public double? AverageDaysBetweenChanges { get; set; }

    /// <summary>Time points with averaged glucose data</summary>
    public List<SiteChangeImpactDataPoint> DataPoints { get; set; } = new();

    /// <summary>Summary statistics comparing before vs after</summary>
    public SiteChangeImpactSummary Summary { get; set; } = new();

    /// <summary>The consensus band edges the chart draws its target range between.</summary>
    public required GlycemicThresholds Thresholds { get; set; }

    /// <summary>Hours analyzed before site change</summary>
    public int HoursBeforeChange { get; set; }

    /// <summary>Hours analyzed after site change</summary>
    public int HoursAfterChange { get; set; }

    /// <summary>Bucket size in minutes for averaging</summary>
    public int BucketSizeMinutes { get; set; }

    /// <summary>Whether sufficient data exists for meaningful analysis</summary>
    public bool HasSufficientData { get; set; }
}

/// <summary>
/// Top-level DTO for AID (Automated Insulin Delivery) system metrics returned by the API endpoint.
/// Contains time-weighted aggregates across mixed device segments within a report period.
/// </summary>
public class AidSystemMetrics
{
    /// <summary>Comma-separated current CGM device names from catalog</summary>
    public string? CgmDeviceNames { get; set; }

    /// <summary>Comma-separated current pump device names from catalog</summary>
    public string? PumpDeviceNames { get; set; }

    /// <summary>Time-weighted pump use percentage across segments</summary>
    public double? PumpUsePercent { get; set; }

    /// <summary>Time-weighted AID active percentage across segments</summary>
    public double? AidActivePercent { get; set; }

    /// <summary>
    /// Percentage of the period a CGM was delivering readings, or null if unknown.
    /// </summary>
    public double? CgmActivePercent { get; set; }

    /// <summary>Lower target bound in mg/dL</summary>
    public double? TargetLow { get; set; }

    /// <summary>Upper target bound in mg/dL</summary>
    public double? TargetHigh { get; set; }

    /// <summary>Number of DeviceEvent SiteChange events in the period</summary>
    public int? SiteChangeCount { get; set; }

    /// <summary>Per-device time segments with individual metrics</summary>
    public List<AidTimeSegment> Segments { get; set; } = new();

    /// <summary>
    /// Why no tenant timezone was available, so device start and end dates were read as UTC days;
    /// null when they followed the tenant's timezone.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>
/// One segment in the time-weighted AID breakdown, representing a period where
/// a single algorithm was active on a specific device.
/// </summary>
public class AidTimeSegment
{
    /// <summary>Which AID algorithm was active during this segment</summary>
    public AidAlgorithm Algorithm { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    /// <summary>Duration of the segment in hours</summary>
    public double DurationHours { get; set; }

    public AidSegmentMetrics Metrics { get; set; } = new();
}

/// <summary>
/// Metrics computed by a strategy for a single AID time segment.
/// </summary>
public class AidSegmentMetrics
{
    /// <summary>Percentage of time AID was active in this segment</summary>
    public double? AidActivePercent { get; set; }

    /// <summary>Percentage of time pump was in use in this segment</summary>
    public double? PumpUsePercent { get; set; }

    /// <summary>Number of loop iterations (applicable to open-source AIDs)</summary>
    public int? LoopCycleCount { get; set; }

    /// <summary>Loop cycles the uploader reported enacted, Loop's open-loop cycles included</summary>
    public int? EnactedCount { get; set; }

    /// <summary>
    /// Loop cycles enacted with the loop closed (<see cref="V4.ApsLoopCycle.EnactedClosedLoop"/>), whose
    /// time is <see cref="AidActivePercent"/>
    /// </summary>
    public int? ClosedLoopCount { get; set; }
}

/// <summary>
/// Input context for a strategy's Calculate method, containing the algorithm,
/// time window, and pre-sliced data for a single detection segment.
/// </summary>
public class AidDetectionContext
{
    /// <summary>Which AID algorithm to detect metrics for</summary>
    public AidAlgorithm Algorithm { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    /// <summary>APS loop cycles within the segment window (for OS AID strategies)</summary>
    public IReadOnlyList<V4.ApsLoopCycle> LoopCycles { get; set; } = [];

    /// <summary>Every temp basal read for the report, not only those in the segment (for TBR-based strategies)</summary>
    public IReadOnlyList<V4.TempBasal> TempBasals { get; set; } = [];
}

/// <summary>
/// Maps a patient device to a time segment for orchestration.
/// Used to split a report period into per-device windows before
/// dispatching to algorithm-specific strategies.
/// </summary>
public class DeviceSegmentInput
{
    public AidAlgorithm Algorithm { get; set; }

    /// <summary>Start of the device's active window</summary>
    public DateTime StartDate { get; set; }

    /// <summary>End of the device's active window</summary>
    public DateTime EndDate { get; set; }
}
