using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Basic glucose statistics
/// </summary>
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

    /// <summary>
    /// Median glucose value
    /// </summary>
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

/// <summary>
/// Glycemic variability metrics
/// </summary>
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

    /// <summary>
    /// Average Daily Risk Range
    /// </summary>
    public double AverageDailyRiskRange { get; set; }

    /// <summary>
    /// Lability Index
    /// </summary>
    public double LabilityIndex { get; set; }

    /// <summary>
    /// J-Index
    /// </summary>
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

/// <summary>
/// Glycemic thresholds for analysis
/// </summary>
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
    /// Tight target range top threshold (default: 140 mg/dL)
    /// </summary>
    public double TightTargetTop { get; set; } = 140;

    /// <summary>
    /// Threshold for high glucose (defaults to <see cref="GlucoseConstants.TargetTopMgdl"/>)
    /// </summary>
    public double High { get; set; } = GlucoseConstants.TargetTopMgdl;

    /// <summary>
    /// Threshold for very high glucose (defaults to <see cref="GlucoseConstants.VeryHighMgdl"/>)
    /// </summary>
    public double VeryHigh { get; set; } = GlucoseConstants.VeryHighMgdl;
}

/// <summary>
/// Time in range metrics
/// </summary>
public class TimeInRangeMetrics
{
    /// <summary>
    /// Percentages of time in each range
    /// </summary>
    public TimeInRangePercentages Percentages { get; set; } = new();

    /// <summary>
    /// Durations in each range (in minutes)
    /// </summary>
    public TimeInRangeDurations Durations { get; set; } = new();

    /// <summary>
    /// Number of episodes in each range
    /// </summary>
    public TimeInRangeEpisodes Episodes { get; set; } = new();

    /// <summary>
    /// Per-range detailed statistics (count, average, median, stdDev)
    /// </summary>
    public TimeInRangeDetailedStats RangeStats { get; set; } = new();
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
/// Time in range percentages
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
}

/// <summary>
/// Extended time in range percentages with 7 glucose ranges for hourly distribution
/// </summary>
public class ExtendedTimeInRangePercentages
{
    /// <summary>
    /// Percentage of time in very low range (less than 54 mg/dL)
    /// </summary>
    public double VeryLow { get; set; }

    /// <summary>
    /// Percentage of time in low range (54-63 mg/dL)
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// Percentage of time in normoglycemic range (63-140 mg/dL)
    /// </summary>
    public double Normal { get; set; }

    /// <summary>
    /// Percentage of time above target but not high (140-180 mg/dL)
    /// </summary>
    public double AboveTarget { get; set; }

    /// <summary>
    /// Percentage of time in high range (180-200 mg/dL)
    /// </summary>
    public double High { get; set; }

    /// <summary>
    /// Percentage of time in very high range (200+ mg/dL)
    /// </summary>
    public double VeryHigh { get; set; }
}

/// <summary>
/// Time in range durations (in minutes)
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
}

/// <summary>
/// Time in range episodes
/// </summary>
public class TimeInRangeEpisodes
{
    /// <summary>
    /// Number of very low episodes
    /// </summary>
    public int VeryLow { get; set; }

    /// <summary>
    /// Number of low episodes
    /// </summary>
    public int Low { get; set; }

    /// <summary>
    /// Number of high episodes
    /// </summary>
    public int High { get; set; }

    /// <summary>
    /// Number of very high episodes
    /// </summary>
    public int VeryHigh { get; set; }
}

/// <summary>
/// Glucose distribution data point
/// </summary>
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

/// <summary>
/// Distribution bin configuration
/// </summary>
public class DistributionBin
{
    /// <summary>
    /// Range description
    /// </summary>
    public string Range { get; set; } = string.Empty;

    /// <summary>
    /// Minimum value for this bin
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// Maximum value for this bin
    /// </summary>
    public double Max { get; set; }
}

/// <summary>
/// Treatment summary for a collection of treatments
/// </summary>
public class TreatmentSummary
{
    /// <summary>
    /// Aggregated treatment totals
    /// </summary>
    public TreatmentTotals Totals { get; set; } = new();

    /// <summary>
    /// Total number of treatment entries
    /// </summary>
    public int TreatmentCount { get; set; }

    /// <summary>
    /// Carbohydrate to insulin ratio (grams of carbs per unit of insulin)
    /// </summary>
    public double CarbToInsulinRatio { get; set; }
}

/// <summary>
/// Treatment totals
/// </summary>
public class TreatmentTotals
{
    /// <summary>
    /// Food-related totals
    /// </summary>
    public FoodTotals Food { get; set; } = new();

    /// <summary>
    /// Insulin-related totals
    /// </summary>
    public InsulinTotals Insulin { get; set; } = new();
}

/// <summary>
/// Food totals
/// </summary>
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
/// Insulin totals
/// </summary>
public class InsulinTotals
{
    /// <summary>
    /// Total bolus insulin in units
    /// </summary>
    public double Bolus { get; set; }

    /// <summary>
    /// Total basal insulin in units (scheduled + additional)
    /// </summary>
    public double Basal { get; set; }

    /// <summary>
    /// Scheduled (profile) basal insulin in units
    /// </summary>
    public double ScheduledBasal { get; set; }

    /// <summary>
    /// Additional basal insulin above scheduled rate (TBR - scheduled)
    /// </summary>
    public double AdditionalBasal { get; set; }
}

/// <summary>
/// Overall averages across multiple days
/// </summary>
public class OverallAverages
{
    /// <summary>
    /// Average total daily insulin
    /// </summary>
    public double AvgTotalDaily { get; set; }

    /// <summary>
    /// Average daily bolus insulin
    /// </summary>
    public double AvgBolus { get; set; }

    /// <summary>
    /// Average daily basal insulin
    /// </summary>
    public double AvgBasal { get; set; }

    /// <summary>
    /// Percentage of total insulin that is bolus
    /// </summary>
    public double BolusPercentage { get; set; }

    /// <summary>
    /// Percentage of total insulin that is basal
    /// </summary>
    public double BasalPercentage { get; set; }

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

    /// <summary>
    /// Treatments for this day
    /// </summary>
    public IEnumerable<Treatment> Treatments { get; set; } = Enumerable.Empty<Treatment>();

    /// <summary>
    /// Summary of treatments for this day
    /// </summary>
    public TreatmentSummary TreatmentSummary { get; set; } = new();

    /// <summary>
    /// Time in range metrics for this day
    /// </summary>
    public TimeInRangeMetrics TimeInRanges { get; set; } = new();
}

/// <summary>
/// Extended analysis configuration
/// </summary>
public class ExtendedAnalysisConfig
{
    /// <summary>
    /// Glycemic thresholds to use for analysis
    /// </summary>
    public GlycemicThresholds Thresholds { get; set; } = new();

    /// <summary>
    /// Type of continuous glucose monitor sensor
    /// </summary>
    public string SensorType { get; set; } = "GENERIC_5MIN";

    /// <summary>
    /// Whether to include looping-specific metrics
    /// </summary>
    public bool IncludeLoopingMetrics { get; set; } = false;

    /// <summary>
    /// Units for glucose values (mg/dl or mmol/l)
    /// </summary>
    public string Units { get; set; } = "mg/dl";
}

/// <summary>
/// Data quality metrics
/// </summary>
public class DataQuality
{
    /// <summary>
    /// Total expected readings
    /// </summary>
    public int TotalReadings { get; set; }

    /// <summary>
    /// Number of missing readings
    /// </summary>
    public int MissingReadings { get; set; }

    /// <summary>
    /// Percentage of data completeness (0-100)
    /// </summary>
    public double DataCompleteness { get; set; }

    /// <summary>
    /// Percentage of time CGM was active (0-100)
    /// </summary>
    public double CgmActivePercent { get; set; }

    /// <summary>
    /// Analysis of data gaps
    /// </summary>
    public GapAnalysis GapAnalysis { get; set; } = new();

    /// <summary>
    /// Noise level in the data (0-1, where 0 is no noise)
    /// </summary>
    public double NoiseLevel { get; set; }

    /// <summary>
    /// Number of calibration events
    /// </summary>
    public int CalibrationEvents { get; set; }

    /// <summary>
    /// Number of sensor warmup periods
    /// </summary>
    public int SensorWarmups { get; set; }
}

/// <summary>
/// Gap analysis in data
/// </summary>
public class GapAnalysis
{
    /// <summary>
    /// Collection of identified data gaps
    /// </summary>
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

/// <summary>
/// Data gap information
/// </summary>
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

/// <summary>
/// Comprehensive glucose analytics result
/// </summary>
public class GlucoseAnalytics
{
    /// <summary>
    /// Basic statistical metrics
    /// </summary>
    public BasicGlucoseStats BasicStats { get; set; } = new();

    /// <summary>
    /// Time in range analysis
    /// </summary>
    public TimeInRangeMetrics TimeInRange { get; set; } = new();

    /// <summary>
    /// Glycemic variability metrics
    /// </summary>
    public GlycemicVariability GlycemicVariability { get; set; } = new();

    /// <summary>
    /// Data quality assessment
    /// </summary>
    public DataQuality DataQuality { get; set; } = new();

    /// <summary>
    /// Reliability assessment for this analysis block
    /// </summary>
    public StatisticReliability? Reliability { get; set; }

    /// <summary>
    /// Time period of the analysis
    /// </summary>
    public AnalysisTime Time { get; set; } = new();

    /// <summary>
    /// Clinical assessment with insights, strengths, and priority areas
    /// </summary>
    public ClinicalTargetAssessment? ClinicalAssessment { get; set; }
}

/// <summary>
/// Analysis time information
/// </summary>
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

    /// <summary>
    /// When the statistics were last updated
    /// </summary>
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

    /// <summary>
    /// Start date of the period
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End date of the period
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Comprehensive glucose analytics for this period
    /// </summary>
    public GlucoseAnalytics? Analytics { get; set; }

    /// <summary>
    /// Treatment summary for this period
    /// </summary>
    public TreatmentSummary? TreatmentSummary { get; set; }

    /// <summary>
    /// Comprehensive insulin delivery statistics for this period
    /// </summary>
    public InsulinDeliveryStatistics? InsulinDelivery { get; set; }

    /// <summary>
    /// Indicates if there was sufficient data for meaningful statistics
    /// </summary>
    public bool HasSufficientData { get; set; }

    /// <summary>
    /// Glucose Management Indicator for this period
    /// </summary>
    public GlucoseManagementIndicator? Gmi { get; set; }

    /// <summary>
    /// Reliability assessment for this period's statistics
    /// </summary>
    public StatisticReliability? Reliability { get; set; }

    /// <summary>
    /// Number of glucose entries in this period
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>
    /// Number of treatments in this period
    /// </summary>
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
    /// Extended time in range percentages with 7 glucose ranges for this hour
    /// </summary>
    public ExtendedTimeInRangePercentages TimeInRange { get; set; } = new();
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
    /// <param name="population">The diabetes population for which to retrieve targets</param>
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
                TargetCV = 36,
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
                TargetCV = 36,
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
                TargetCV = 36,
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
                TargetCV = 36,
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
                TargetCV = 36,
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

    /// <summary>Interpretation of the GMI value</summary>
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
/// GRI = (3.0 × VLow%) + (2.4 × Low%) + (1.6 × VHigh%) + (0.8 × High%)
/// </summary>
public class GlycemicRiskIndex
{
    /// <summary>Overall GRI score (0-100, lower is better)</summary>
    public double Score { get; set; }

    /// <summary>Hypoglycemia component of the score</summary>
    public double HypoglycemiaComponent { get; set; }

    /// <summary>Hyperglycemia component of the score</summary>
    public double HyperglycemiaComponent { get; set; }

    /// <summary>Risk zone classification</summary>
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

    /// <summary>Start hour of the period (0-23)</summary>
    public int StartHour { get; set; }

    /// <summary>End hour of the period (0-23)</summary>
    public int EndHour { get; set; }

    /// <summary>Number of readings in this period</summary>
    public int ReadingCount { get; set; }

    /// <summary>Mean glucose (mg/dL)</summary>
    public double Mean { get; set; }

    /// <summary>Median glucose (mg/dL)</summary>
    public double Median { get; set; }

    /// <summary>Standard deviation (mg/dL)</summary>
    public double StandardDeviation { get; set; }

    /// <summary>Coefficient of variation (%)</summary>
    public double CoefficientOfVariation { get; set; }

    /// <summary>Time in range percentage (%)</summary>
    public double TimeInRange { get; set; }

    /// <summary>Time below range percentage (%)</summary>
    public double TimeBelowRange { get; set; }

    /// <summary>Time very low percentage (%)</summary>
    public double TimeVeryLow { get; set; }

    /// <summary>Time above range percentage (%)</summary>
    public double TimeAboveRange { get; set; }

    /// <summary>Time very high percentage (%)</summary>
    public double TimeVeryHigh { get; set; }

    /// <summary>Number of hypoglycemic events in this period</summary>
    public int HypoglycemiaEvents { get; set; }

    /// <summary>Number of hyperglycemic events in this period</summary>
    public int HyperglycemiaEvents { get; set; }

    /// <summary>Minimum glucose value (mg/dL)</summary>
    public double Min { get; set; }

    /// <summary>Maximum glucose value (mg/dL)</summary>
    public double Max { get; set; }
}

/// <summary>
/// Individual hyperglycemia episode details
/// </summary>
public class HyperglycemiaEpisode
{
    /// <summary>Start time of the episode (Unix milliseconds)</summary>
    public long StartTime { get; set; }

    /// <summary>End time of the episode (Unix milliseconds)</summary>
    public long EndTime { get; set; }

    /// <summary>Duration of the episode in minutes</summary>
    public double DurationMinutes { get; set; }

    /// <summary>Peak glucose value during the episode (mg/dL)</summary>
    public double PeakValue { get; set; }

    /// <summary>Time of peak (Unix milliseconds)</summary>
    public long PeakTime { get; set; }

    /// <summary>Whether this was a severe episode (&gt;250 mg/dL)</summary>
    public bool IsSevere { get; set; }

    /// <summary>Whether this was a prolonged episode (&gt;2 hours)</summary>
    public bool IsProlonged { get; set; }

    /// <summary>Hour of day when episode started (0-23)</summary>
    public int HourOfDay { get; set; }

    /// <summary>Day of week when episode occurred</summary>
    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Time to return to target range in minutes</summary>
    public double TimeToTargetMinutes { get; set; }

    /// <summary>Average glucose during the episode</summary>
    public double AverageGlucose { get; set; }
}

/// <summary>
/// Comprehensive hyperglycemia analysis
/// </summary>
public class HyperglycemiaAnalysis
{
    /// <summary>Total number of hyperglycemia episodes (&gt;180 mg/dL)</summary>
    public int TotalEpisodes { get; set; }

    /// <summary>Number of severe hyperglycemia episodes (&gt;250 mg/dL)</summary>
    public int SevereEpisodes { get; set; }

    /// <summary>Number of prolonged episodes (&gt;2 hours above 180)</summary>
    public int ProlongedEpisodes { get; set; }

    /// <summary>Average episodes per day</summary>
    public double EpisodesPerDay { get; set; }

    /// <summary>Average duration of episodes in minutes</summary>
    public double AverageDurationMinutes { get; set; }

    /// <summary>Average peak glucose during episodes</summary>
    public double AveragePeak { get; set; }

    /// <summary>Highest glucose recorded</summary>
    public double HighestGlucose { get; set; }

    /// <summary>Average time to return to target range</summary>
    public double AverageTimeToTargetMinutes { get; set; }

    /// <summary>Time of day distribution of episodes (hour -> count)</summary>
    public Dictionary<int, int> HourlyDistribution { get; set; } = new();

    /// <summary>Day of week distribution of episodes</summary>
    public Dictionary<DayOfWeek, int> DayOfWeekDistribution { get; set; } = new();

    /// <summary>Most common hour for hyperglycemia</summary>
    public int? PeakHour { get; set; }

    /// <summary>Most common day for hyperglycemia</summary>
    public DayOfWeek? PeakDay { get; set; }

    /// <summary>Whether there's a post-meal pattern</summary>
    public bool HasPostMealPattern { get; set; }

    /// <summary>Description of the pattern if detected</summary>
    public string PatternDescription { get; set; } = string.Empty;

    /// <summary>List of individual episodes</summary>
    public List<HyperglycemiaEpisode> Episodes { get; set; } = new();

    /// <summary>Nocturnal hyperglycemia episodes (12 AM - 6 AM)</summary>
    public int NocturnalEpisodes { get; set; }

    /// <summary>Percentage of total episodes that are nocturnal</summary>
    public double NocturnalPercentage { get; set; }
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

    /// <summary>Number of days with glucose data in this analysis window</summary>
    public int DaysOfData { get; set; }

    /// <summary>Minimum days recommended by clinical guidelines for reliable results</summary>
    public int RecommendedMinimumDays { get; set; }

    /// <summary>Number of glucose readings used in this analysis</summary>
    public int ReadingCount { get; set; }
}

/// <summary>
/// Target achievement status
/// </summary>
public enum TargetStatus
{
    /// <summary>Target met</summary>
    Met,

    /// <summary>Close to target (within 10%)</summary>
    Close,

    /// <summary>Target not met</summary>
    NotMet,
}

/// <summary>
/// Individual target assessment
/// </summary>
public class TargetAssessment
{
    /// <summary>Name of the metric</summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>Current value</summary>
    public double CurrentValue { get; set; }

    /// <summary>Target value</summary>
    public double TargetValue { get; set; }

    /// <summary>Whether target is a maximum (true) or minimum (false)</summary>
    public bool IsMaximumTarget { get; set; }

    /// <summary>Status of target achievement</summary>
    public TargetStatus Status { get; set; }

    /// <summary>Difference from target</summary>
    public double DifferenceFromTarget { get; set; }

    /// <summary>Percentage progress toward target</summary>
    public double ProgressPercentage { get; set; }
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

/// <summary>
/// Comprehensive clinical target assessment
/// </summary>
public class ClinicalTargetAssessment
{
    /// <summary>Population type used for targets</summary>
    public DiabetesPopulation Population { get; set; }

    /// <summary>Clinical targets used for assessment</summary>
    public ClinicalTargets Targets { get; set; } = new();

    /// <summary>Time in range assessment</summary>
    public TargetAssessment TIRAssessment { get; set; } = new();

    /// <summary>Time below range assessment</summary>
    public TargetAssessment TBRAssessment { get; set; } = new();

    /// <summary>Time very low assessment</summary>
    public TargetAssessment VeryLowAssessment { get; set; } = new();

    /// <summary>Time above range assessment</summary>
    public TargetAssessment TARAssessment { get; set; } = new();

    /// <summary>Time very high assessment</summary>
    public TargetAssessment VeryHighAssessment { get; set; } = new();

    /// <summary>Coefficient of variation assessment</summary>
    public TargetAssessment CVAssessment { get; set; } = new();

    /// <summary>Number of targets met</summary>
    public int TargetsMet { get; set; }

    /// <summary>Total number of targets assessed</summary>
    public int TotalTargets { get; set; }

    /// <summary>Overall assessment category</summary>
    public ClinicalAssessmentLevel OverallAssessment { get; set; }

    /// <summary>List of actionable insights/recommendations with localization keys and context</summary>
    public List<LocalizedInsight> ActionableInsights { get; set; } = new();

    /// <summary>Priority areas for improvement with localization keys and context</summary>
    public List<LocalizedInsight> PriorityAreas { get; set; } = new();

    /// <summary>Strengths/achievements to acknowledge with localization keys and context</summary>
    public List<LocalizedInsight> Strengths { get; set; } = new();
}

/// <summary>
/// Data sufficiency assessment for reporting
/// </summary>
public class DataSufficiencyAssessment
{
    /// <summary>Whether there is sufficient data for a valid report</summary>
    public bool IsSufficient { get; set; }

    /// <summary>Number of days in the period</summary>
    public int TotalDays { get; set; }

    /// <summary>Number of days with any data</summary>
    public int DaysWithData { get; set; }

    /// <summary>Expected number of readings (based on sensor type)</summary>
    public int ExpectedReadings { get; set; }

    /// <summary>Actual number of readings</summary>
    public int ActualReadings { get; set; }

    /// <summary>Data completeness percentage</summary>
    public double CompletenessPercentage { get; set; }

    /// <summary>Minimum required completeness for valid report (typically 70%)</summary>
    public double MinimumRequiredCompleteness { get; set; } = 70;

    /// <summary>Average readings per day</summary>
    public double AverageReadingsPerDay { get; set; }

    /// <summary>Longest data gap in hours</summary>
    public double LongestGapHours { get; set; }

    /// <summary>Data sufficiency status (use this for localization on frontend)</summary>
    public DataSufficiencyStatus Status { get; set; } = DataSufficiencyStatus.Sufficient;

    /// <summary>Warning message if data is insufficient</summary>
    public string WarningMessage { get; set; } = string.Empty;

    /// <summary>Recommendation for improving data collection</summary>
    public string Recommendation { get; set; } = string.Empty;
}

/// <summary>
/// Extended glucose analytics with all new metrics
/// </summary>
public class ExtendedGlucoseAnalytics : GlucoseAnalytics
{
    /// <summary>Glucose Management Indicator (modern replacement for eA1c)</summary>
    public GlucoseManagementIndicator GMI { get; set; } = new();

    /// <summary>Glycemic Risk Index (composite risk score)</summary>
    public GlycemicRiskIndex GRI { get; set; } = new();

    /// <summary>Hyperglycemia event analysis</summary>
    public HyperglycemiaAnalysis HyperglycemiaAnalysis { get; set; } = new();

    /// <summary>Clinical target assessment</summary>
    public new ClinicalTargetAssessment ClinicalAssessment { get; set; } = new();

    /// <summary>Data sufficiency assessment</summary>
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
    /// <summary>Extended glucose analytics (TIR, GMI, GRI, variability, hypo/hyper, etc.).</summary>
    public ExtendedGlucoseAnalytics Analysis { get; set; } = new();

    /// <summary>Time-of-day averaged statistics for AGP-style charts.</summary>
    public IEnumerable<AveragedStats> AveragedStats { get; set; } = [];

    /// <summary>Registered devices that contributed readings within the requested window, for device-picker UIs.</summary>
    public List<ContributingDevice> ContributingDevices { get; set; } = new();

    /// <summary>
    /// Time in the tenant's personal target range schedule, alongside the clinical-consensus
    /// TIR in <see cref="Analysis"/>. Null when the tenant has no target range schedule
    /// configured or the window has no readings.
    /// </summary>
    public PersonalRangeTimeInRange? PersonalRange { get; set; }
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

    /// <summary>Display name of the device, or "Unattributed" for readings with no <see cref="PatientDeviceId"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of readings attributed to this device within the requested window.</summary>
    public int ReadingCount { get; set; }
}

/// <summary>
/// Data point for site change impact analysis
/// Represents averaged glucose at a specific time offset from site change
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

/// <summary>
/// Summary statistics for site change impact
/// </summary>
public class SiteChangeImpactSummary
{
    /// <summary>Average glucose before site change (mg/dL)</summary>
    public double AvgGlucoseBeforeChange { get; set; }

    /// <summary>Average glucose after site change (mg/dL)</summary>
    public double AvgGlucoseAfterChange { get; set; }

    /// <summary>Percent improvement in glucose after site change</summary>
    public double PercentImprovement { get; set; }

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
    /// Total bolus insulin in units for the period
    /// </summary>
    public double TotalBolus { get; set; }

    /// <summary>
    /// Total basal insulin in units for the period (scheduled + additional)
    /// </summary>
    public double TotalBasal { get; set; }

    /// <summary>
    /// Scheduled (profile) basal insulin in units for the period
    /// </summary>
    public double ScheduledBasal { get; set; }

    /// <summary>
    /// Additional basal insulin above scheduled rate (TBR - scheduled) for the period
    /// </summary>
    public double AdditionalBasal { get; set; }

    /// <summary>
    /// Total insulin (bolus + basal) for the period
    /// </summary>
    public double TotalInsulin { get; set; }

    /// <summary>
    /// Total carbohydrates consumed in grams
    /// </summary>
    public double TotalCarbs { get; set; }

    /// <summary>
    /// Number of bolus treatments
    /// </summary>
    public int BolusCount { get; set; }

    /// <summary>
    /// Number of basal treatments
    /// </summary>
    public int BasalCount { get; set; }

    /// <summary>
    /// Percentage of total insulin that is basal (0-100)
    /// </summary>
    public double BasalPercent { get; set; }

    /// <summary>
    /// Percentage of total insulin that is bolus (0-100)
    /// </summary>
    public double BolusPercent { get; set; }

    /// <summary>
    /// Average Total Daily Dose (units per day)
    /// </summary>
    public double Tdd { get; set; }

    /// <summary>
    /// Average bolus size (units)
    /// </summary>
    public double AvgBolus { get; set; }

    /// <summary>
    /// Number of meal boluses (based on event type)
    /// </summary>
    public int MealBoluses { get; set; }

    /// <summary>
    /// Number of correction boluses (based on event type)
    /// </summary>
    public int CorrectionBoluses { get; set; }

    /// <summary>
    /// Insulin to carb ratio (grams of carbs per unit of insulin)
    /// </summary>
    public double IcRatio { get; set; }

    /// <summary>
    /// Average number of boluses per day
    /// </summary>
    public double BolusesPerDay { get; set; }

    /// <summary>
    /// Number of days in the analysis period
    /// </summary>
    public int DayCount { get; set; }

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
    /// Number of algorithm-delivered micro-boluses (SMBs) in the period
    /// </summary>
    public int MicroBolusCount { get; set; }

    /// <summary>
    /// Total insulin delivered via micro-boluses (SMBs) in units
    /// </summary>
    public double MicroBolusInsulin { get; set; }

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
    /// Reliability assessment for insulin delivery statistics
    /// </summary>
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
    /// Basal insulin delivered in units
    /// </summary>
    public double Basal { get; set; }

    /// <summary>
    /// Bolus insulin delivered in units
    /// </summary>
    public double Bolus { get; set; }

    /// <summary>
    /// Total insulin (basal + bolus) in units
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// Basal as percentage of total (0-100)
    /// </summary>
    public double BasalPercent { get; set; }

    /// <summary>
    /// Bolus as percentage of total (0-100)
    /// </summary>
    public double BolusPercent { get; set; }
}

/// <summary>
/// Response model for daily basal/bolus ratio statistics
/// </summary>
public class DailyBasalBolusRatioResponse
{
    /// <summary>
    /// Daily breakdown of basal/bolus ratios
    /// </summary>
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
    /// Number of days with data
    /// </summary>
    public int DayCount { get; set; }
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

/// <summary>
/// Temp basal information statistics
/// </summary>
public class TempBasalInfo
{
    /// <summary>
    /// Total number of temp basals in the period
    /// </summary>
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
    /// <summary>
    /// Number of basal events
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Average basal rate in U/hr
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
    /// Total basal insulin delivered in units
    /// </summary>
    public double TotalDelivered { get; set; }
}

/// <summary>
/// Comprehensive basal analysis response
/// </summary>
public class BasalAnalysisResponse
{
    /// <summary>
    /// Basic basal statistics
    /// </summary>
    public BasalStats Stats { get; set; } = new();

    /// <summary>
    /// Temp basal information
    /// </summary>
    public TempBasalInfo TempBasalInfo { get; set; } = new();

    /// <summary>
    /// Hourly basal rate percentiles for chart rendering
    /// </summary>
    public List<HourlyBasalPercentileData> HourlyPercentiles { get; set; } = new();

    /// <summary>
    /// Number of days in the analysis period
    /// </summary>
    public int DayCount { get; set; }

    /// <summary>
    /// Start date of the analysis period
    /// </summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    /// End date of the analysis period
    /// </summary>
    public string EndDate { get; set; } = string.Empty;
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
    /// and algorithm micro-boluses
    /// </summary>
    public double TempBasal { get; set; }

    /// <summary>
    /// Average total basal insulin (scheduled + temp)
    /// </summary>
    public double Basal { get; set; }

    /// <summary>
    /// Average insulin from user boluses
    /// </summary>
    public double Bolus { get; set; }

    /// <summary>
    /// Average total insulin delivered during this hour
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// Number of delivery records contributing to this hour
    /// </summary>
    public int Count { get; set; }
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
    /// Number of distinct days with delivery data the averages are taken over
    /// </summary>
    public int DayCount { get; set; }

    /// <summary>
    /// Start date of the analysis period
    /// </summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    /// End date of the analysis period
    /// </summary>
    public string EndDate { get; set; } = string.Empty;
}

/// <summary>
/// Complete site change impact analysis result
/// </summary>
public class SiteChangeImpactAnalysis
{
    /// <summary>Number of site changes analyzed</summary>
    public int SiteChangeCount { get; set; }

    /// <summary>Average number of days between site changes</summary>
    public double? AverageDaysBetweenChanges { get; set; }

    /// <summary>Time points with averaged glucose data</summary>
    public List<SiteChangeImpactDataPoint> DataPoints { get; set; } = new();

    /// <summary>Summary statistics comparing before vs after</summary>
    public SiteChangeImpactSummary Summary { get; set; } = new();

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

    /// <summary>CGM data completeness percentage</summary>
    public double? CgmActivePercent { get; set; }

    /// <summary>Lower target bound in mg/dL</summary>
    public double? TargetLow { get; set; }

    /// <summary>Upper target bound in mg/dL</summary>
    public double? TargetHigh { get; set; }

    /// <summary>Number of DeviceEvent SiteChange events in the period</summary>
    public int? SiteChangeCount { get; set; }

    /// <summary>Per-device time segments with individual metrics</summary>
    public List<AidTimeSegment> Segments { get; set; } = new();
}

/// <summary>
/// One segment in the time-weighted AID breakdown, representing a period where
/// a single algorithm was active on a specific device.
/// </summary>
public class AidTimeSegment
{
    /// <summary>Which AID algorithm was active during this segment</summary>
    public AidAlgorithm Algorithm { get; set; }

    /// <summary>Start of the segment</summary>
    public DateTime StartDate { get; set; }

    /// <summary>End of the segment</summary>
    public DateTime EndDate { get; set; }

    /// <summary>Duration of the segment in hours</summary>
    public double DurationHours { get; set; }

    /// <summary>Computed metrics for this segment</summary>
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

    /// <summary>Number of enacted suggestions</summary>
    public int? EnactedCount { get; set; }
}

/// <summary>
/// Input context for a strategy's Calculate method, containing the algorithm,
/// time window, and pre-sliced data for a single detection segment.
/// </summary>
public class AidDetectionContext
{
    /// <summary>Which AID algorithm to detect metrics for</summary>
    public AidAlgorithm Algorithm { get; set; }

    /// <summary>Start of the detection window</summary>
    public DateTime StartDate { get; set; }

    /// <summary>End of the detection window</summary>
    public DateTime EndDate { get; set; }

    /// <summary>APS snapshots within the segment window (for OS AID strategies)</summary>
    public IReadOnlyList<V4.ApsSnapshot> ApsSnapshots { get; set; } = [];

    /// <summary>Temp basals within the segment window (for TBR-based strategies)</summary>
    public IReadOnlyList<V4.TempBasal> TempBasals { get; set; } = [];
}

/// <summary>
/// Maps a patient device to a time segment for orchestration.
/// Used to split a report period into per-device windows before
/// dispatching to algorithm-specific strategies.
/// </summary>
public class DeviceSegmentInput
{
    /// <summary>Which AID algorithm this device uses</summary>
    public AidAlgorithm Algorithm { get; set; }

    /// <summary>Start of the device's active window</summary>
    public DateTime StartDate { get; set; }

    /// <summary>End of the device's active window</summary>
    public DateTime EndDate { get; set; }
}
