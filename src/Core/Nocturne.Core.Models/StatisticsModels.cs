using System.Text.Json.Serialization;

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
    /// Threshold for severe low glucose (default: 54 mg/dL)
    /// </summary>
    public double SevereLow { get; set; } = 54;

    /// <summary>
    /// Threshold for low glucose (default: 70 mg/dL)
    /// </summary>
    public double Low { get; set; } = 70;

    /// <summary>
    /// Target range bottom threshold (default: 70 mg/dL)
    /// </summary>
    public double TargetBottom { get; set; } = 70;

    /// <summary>
    /// Target range top threshold (default: 180 mg/dL)
    /// </summary>
    public double TargetTop { get; set; } = 180;

    /// <summary>
    /// Tight target range bottom threshold (default: 70 mg/dL)
    /// </summary>
    public double TightTargetBottom { get; set; } = 70;

    /// <summary>
    /// Tight target range top threshold (default: 140 mg/dL)
    /// </summary>
    public double TightTargetTop { get; set; } = 140;

    /// <summary>
    /// Threshold for high glucose (default: 180 mg/dL)
    /// </summary>
    public double High { get; set; } = 180;

    /// <summary>
    /// Threshold for severe high glucose (default: 250 mg/dL)
    /// </summary>
    public double SevereHigh { get; set; } = 250;
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
    /// Percentage of time in severe low range (less than 54 mg/dL)
    /// </summary>
    public double SevereLow { get; set; }

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
    /// Percentage of time in severe high range (greater than 250 mg/dL)
    /// </summary>
    public double SevereHigh { get; set; }
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
    /// Percentage of time in very high range (200-220 mg/dL)
    /// </summary>
    public double VeryHigh { get; set; }

    /// <summary>
    /// Percentage of time in severe high range (greater than 220 mg/dL)
    /// </summary>
    public double SevereHigh { get; set; }
}

/// <summary>
/// Time in range durations (in minutes)
/// </summary>
public class TimeInRangeDurations
{
    /// <summary>
    /// Duration in severe low range (minutes)
    /// </summary>
    public double SevereLow { get; set; }

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
    /// Duration in severe high range (minutes)
    /// </summary>
    public double SevereHigh { get; set; }
}

/// <summary>
/// Time in range episodes
/// </summary>
public class TimeInRangeEpisodes
{
    /// <summary>
    /// Number of severe low episodes
    /// </summary>
    public int SevereLow { get; set; }

    /// <summary>
    /// Number of low episodes
    /// </summary>
    public int Low { get; set; }

    /// <summary>
    /// Number of high episodes
    /// </summary>
    public int High { get; set; }

    /// <summary>
    /// Number of severe high episodes
    /// </summary>
    public int SevereHigh { get; set; }
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
/// Averaged statistics for a specific hour
/// </summary>
public class HourlyAveragedStats : BasicGlucoseStats
{
    /// <summary>
    /// Hour of the day (0-23)
    /// </summary>
    public int Hour { get; set; }
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
    /// Total basal insulin in units
    /// </summary>
    public double Basal { get; set; }
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
    /// Time period of the analysis
    /// </summary>
    public AnalysisTime Time { get; set; } = new();
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
    /// Get clinical targets for a specific diabetes population
    /// Based on International Consensus on Time in Range (2019) and subsequent updates
    /// </summary>
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
                TargetLow = 70,
                TargetHigh = 180,
            },
            DiabetesPopulation.Type1Pediatric => new ClinicalTargets
            {
                TargetTIR = 70,
                MaxTBR = 4,
                MaxTBRVeryLow = 1,
                MaxTAR = 25,
                MaxTARVeryHigh = 5,
                TargetCV = 36,
                TargetLow = 70,
                TargetHigh = 180,
            },
            DiabetesPopulation.Elderly => new ClinicalTargets
            {
                TargetTIR = 50,
                MaxTBR = 1,
                MaxTBRVeryLow = 0,
                MaxTAR = 50,
                MaxTARVeryHigh = 10,
                TargetCV = 36,
                TargetLow = 70,
                TargetHigh = 180,
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
    public string Interpretation { get; set; } = string.Empty;

    /// <summary>
    /// GMI interpretation categories based on ADA Standards
    /// </summary>
    public static string GetInterpretation(double gmi)
    {
        return gmi switch
        {
            < 5.7 => "Non-diabetic range",
            < 6.5 => "Prediabetes range",
            < 7.0 => "Well-controlled diabetes",
            < 8.0 => "Moderate control",
            < 9.0 => "Suboptimal control",
            _ => "Poor control - intervention recommended",
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
    public string Interpretation { get; set; } = string.Empty;
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
/// Analysis of glucose patterns by time of day
/// </summary>
public class TimeOfDayAnalysis
{
    /// <summary>Overnight period metrics (12:00 AM - 6:00 AM)</summary>
    public PeriodMetrics Overnight { get; set; } =
        new()
        {
            PeriodName = "Overnight",
            StartHour = 0,
            EndHour = 6,
        };

    /// <summary>Morning period metrics (6:00 AM - 12:00 PM)</summary>
    public PeriodMetrics Morning { get; set; } =
        new()
        {
            PeriodName = "Morning",
            StartHour = 6,
            EndHour = 12,
        };

    /// <summary>Afternoon period metrics (12:00 PM - 6:00 PM)</summary>
    public PeriodMetrics Afternoon { get; set; } =
        new()
        {
            PeriodName = "Afternoon",
            StartHour = 12,
            EndHour = 18,
        };

    /// <summary>Evening period metrics (6:00 PM - 12:00 AM)</summary>
    public PeriodMetrics Evening { get; set; } =
        new()
        {
            PeriodName = "Evening",
            StartHour = 18,
            EndHour = 24,
        };

    /// <summary>Dawn phenomenon detected (rising glucose 3-6 AM)</summary>
    public bool DawnPhenomenonDetected { get; set; }

    /// <summary>Magnitude of dawn phenomenon rise (mg/dL)</summary>
    public double DawnPhenomenonMagnitude { get; set; }

    /// <summary>Period with highest variability</summary>
    public string HighestVariabilityPeriod { get; set; } = string.Empty;

    /// <summary>Period with lowest time in range</summary>
    public string LowestTIRPeriod { get; set; } = string.Empty;

    /// <summary>Period with most hypoglycemia events</summary>
    public string MostHypoglycemiaPeriod { get; set; } = string.Empty;
}

/// <summary>
/// Day-specific metrics for day-of-week analysis
/// </summary>
public class DayMetrics : PeriodMetrics
{
    /// <summary>Day of week (0 = Sunday, 6 = Saturday)</summary>
    public DayOfWeek DayOfWeek { get; set; }
}

/// <summary>
/// Analysis of glucose patterns by day of week
/// </summary>
public class DayOfWeekAnalysis
{
    /// <summary>Metrics for each day of the week</summary>
    public Dictionary<DayOfWeek, DayMetrics> DayMetrics { get; set; } = new();

    /// <summary>Average weekday metrics (Monday-Friday)</summary>
    public PeriodMetrics WeekdayAverage { get; set; } = new() { PeriodName = "Weekday Average" };

    /// <summary>Average weekend metrics (Saturday-Sunday)</summary>
    public PeriodMetrics WeekendAverage { get; set; } = new() { PeriodName = "Weekend Average" };

    /// <summary>Day with highest variability</summary>
    public DayOfWeek? HighestVariabilityDay { get; set; }

    /// <summary>Day with lowest time in range</summary>
    public DayOfWeek? LowestTIRDay { get; set; }

    /// <summary>Significant difference between weekday and weekend patterns</summary>
    public bool WeekdayWeekendDifference { get; set; }

    /// <summary>Description of weekday vs weekend pattern</summary>
    public string PatternDescription { get; set; } = string.Empty;
}

/// <summary>
/// Individual hypoglycemia episode details
/// </summary>
public class HypoglycemiaEpisode
{
    /// <summary>Start time of the episode (Unix milliseconds)</summary>
    public long StartTime { get; set; }

    /// <summary>End time of the episode (Unix milliseconds)</summary>
    public long EndTime { get; set; }

    /// <summary>Duration of the episode in minutes</summary>
    public double DurationMinutes { get; set; }

    /// <summary>Lowest glucose value during the episode (mg/dL)</summary>
    public double NadirValue { get; set; }

    /// <summary>Time of nadir (Unix milliseconds)</summary>
    public long NadirTime { get; set; }

    /// <summary>Whether this was a severe episode (&lt;54 mg/dL)</summary>
    public bool IsSevere { get; set; }

    /// <summary>Hour of day when episode started (0-23)</summary>
    public int HourOfDay { get; set; }

    /// <summary>Day of week when episode occurred</summary>
    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Time to recover to >70 mg/dL in minutes</summary>
    public double RecoveryTimeMinutes { get; set; }

    /// <summary>Glucose value before the episode (mg/dL, if available)</summary>
    public double? PreEpisodeGlucose { get; set; }
}

/// <summary>
/// Comprehensive hypoglycemia analysis
/// </summary>
public class HypoglycemiaAnalysis
{
    /// <summary>Total number of hypoglycemia episodes (&lt;70 mg/dL)</summary>
    public int TotalEpisodes { get; set; }

    /// <summary>Number of severe hypoglycemia episodes (&lt;54 mg/dL)</summary>
    public int SevereEpisodes { get; set; }

    /// <summary>Average episodes per day</summary>
    public double EpisodesPerDay { get; set; }

    /// <summary>Average duration of episodes in minutes</summary>
    public double AverageDurationMinutes { get; set; }

    /// <summary>Average nadir (lowest) glucose during episodes</summary>
    public double AverageNadir { get; set; }

    /// <summary>Lowest glucose recorded</summary>
    public double LowestGlucose { get; set; }

    /// <summary>Average time to recover above 70 mg/dL</summary>
    public double AverageRecoveryTimeMinutes { get; set; }

    /// <summary>Time of day distribution of episodes (hour -> count)</summary>
    public Dictionary<int, int> HourlyDistribution { get; set; } = new();

    /// <summary>Day of week distribution of episodes</summary>
    public Dictionary<DayOfWeek, int> DayOfWeekDistribution { get; set; } = new();

    /// <summary>Most common hour for hypoglycemia</summary>
    public int? PeakHour { get; set; }

    /// <summary>Most common day for hypoglycemia</summary>
    public DayOfWeek? PeakDay { get; set; }

    /// <summary>Whether there's a recurring pattern</summary>
    public bool HasRecurringPattern { get; set; }

    /// <summary>Description of the recurring pattern if detected</summary>
    public string PatternDescription { get; set; } = string.Empty;

    /// <summary>List of individual episodes</summary>
    public List<HypoglycemiaEpisode> Episodes { get; set; } = new();

    /// <summary>Nocturnal hypoglycemia episodes (12 AM - 6 AM)</summary>
    public int NocturnalEpisodes { get; set; }

    /// <summary>Percentage of total hypos that are nocturnal</summary>
    public double NocturnalPercentage { get; set; }

    /// <summary>Clinical risk assessment</summary>
    public string RiskAssessment { get; set; } = string.Empty;
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
/// Trend direction indicator
/// </summary>
public enum TrendDirection
{
    /// <summary>Significant improvement</summary>
    Improving,

    /// <summary>Slight improvement</summary>
    SlightlyImproving,

    /// <summary>No significant change</summary>
    Stable,

    /// <summary>Slight decline</summary>
    SlightlyDeclining,

    /// <summary>Significant decline</summary>
    Declining,
}

/// <summary>
/// Comparison between two time periods
/// </summary>
public class TrendComparison
{
    /// <summary>Current period analytics</summary>
    public GlucoseAnalytics CurrentPeriod { get; set; } = new();

    /// <summary>Previous period analytics</summary>
    public GlucoseAnalytics PreviousPeriod { get; set; } = new();

    /// <summary>Change in GMI (current - previous)</summary>
    public double GMIDelta { get; set; }

    /// <summary>Change in time in range (current - previous)</summary>
    public double TIRDelta { get; set; }

    /// <summary>Change in time below range (current - previous)</summary>
    public double TBRDelta { get; set; }

    /// <summary>Change in time above range (current - previous)</summary>
    public double TARDelta { get; set; }

    /// <summary>Change in coefficient of variation (current - previous)</summary>
    public double CVDelta { get; set; }

    /// <summary>Change in GRI score (current - previous)</summary>
    public double GRIDelta { get; set; }

    /// <summary>Overall trend direction for GMI</summary>
    public TrendDirection GMITrend { get; set; }

    /// <summary>Overall trend direction for TIR</summary>
    public TrendDirection TIRTrend { get; set; }

    /// <summary>Overall trend direction for TBR (improvement = decreasing)</summary>
    public TrendDirection TBRTrend { get; set; }

    /// <summary>Overall trend direction for CV</summary>
    public TrendDirection CVTrend { get; set; }

    /// <summary>Overall trend direction for GRI (improvement = decreasing)</summary>
    public TrendDirection GRITrend { get; set; }

    /// <summary>Number of days in current period with TIR >70%</summary>
    public int DaysInRangeCurrent { get; set; }

    /// <summary>Number of days in previous period with TIR >70%</summary>
    public int DaysInRangePrevious { get; set; }

    /// <summary>Summary interpretation of overall trend</summary>
    public string TrendSummary { get; set; } = string.Empty;
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
    public string OverallAssessment { get; set; } = string.Empty;

    /// <summary>List of actionable insights/recommendations</summary>
    public List<string> ActionableInsights { get; set; } = new();

    /// <summary>Priority areas for improvement</summary>
    public List<string> PriorityAreas { get; set; } = new();

    /// <summary>Strengths/achievements to acknowledge</summary>
    public List<string> Strengths { get; set; } = new();
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

    /// <summary>Time-of-day pattern analysis</summary>
    public TimeOfDayAnalysis TimeOfDayPatterns { get; set; } = new();

    /// <summary>Day-of-week pattern analysis</summary>
    public DayOfWeekAnalysis DayOfWeekPatterns { get; set; } = new();

    /// <summary>Hypoglycemia event analysis</summary>
    public HypoglycemiaAnalysis HypoglycemiaAnalysis { get; set; } = new();

    /// <summary>Hyperglycemia event analysis</summary>
    public HyperglycemiaAnalysis HyperglycemiaAnalysis { get; set; } = new();

    /// <summary>Clinical target assessment</summary>
    public ClinicalTargetAssessment ClinicalAssessment { get; set; } = new();

    /// <summary>Data sufficiency assessment</summary>
    public DataSufficiencyAssessment DataSufficiency { get; set; } = new();

    /// <summary>Treatment summary if treatment data is available</summary>
    public TreatmentSummary? TreatmentSummary { get; set; }
}

/// <summary>
/// Data point for site change impact analysis
/// Represents averaged glucose at a specific time offset from site change
/// </summary>
public class SiteChangeImpactDataPoint
{
    /// <summary>Minutes from site change (negative = before, positive = after)</summary>
    [JsonPropertyName("minutesFromChange")]
    public int MinutesFromChange { get; set; }

    /// <summary>Average glucose at this time offset (mg/dL)</summary>
    [JsonPropertyName("averageGlucose")]
    public double AverageGlucose { get; set; }

    /// <summary>Median glucose at this time offset (mg/dL)</summary>
    [JsonPropertyName("medianGlucose")]
    public double MedianGlucose { get; set; }

    /// <summary>Standard deviation of glucose values</summary>
    [JsonPropertyName("stdDev")]
    public double StdDev { get; set; }

    /// <summary>Number of readings contributing to this data point</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>10th percentile glucose value (mg/dL)</summary>
    [JsonPropertyName("percentile10")]
    public double Percentile10 { get; set; }

    /// <summary>25th percentile glucose value (mg/dL)</summary>
    [JsonPropertyName("percentile25")]
    public double Percentile25 { get; set; }

    /// <summary>75th percentile glucose value (mg/dL)</summary>
    [JsonPropertyName("percentile75")]
    public double Percentile75 { get; set; }

    /// <summary>90th percentile glucose value (mg/dL)</summary>
    [JsonPropertyName("percentile90")]
    public double Percentile90 { get; set; }
}

/// <summary>
/// Summary statistics for site change impact
/// </summary>
public class SiteChangeImpactSummary
{
    /// <summary>Average glucose before site change (mg/dL)</summary>
    [JsonPropertyName("avgGlucoseBeforeChange")]
    public double AvgGlucoseBeforeChange { get; set; }

    /// <summary>Average glucose after site change (mg/dL)</summary>
    [JsonPropertyName("avgGlucoseAfterChange")]
    public double AvgGlucoseAfterChange { get; set; }

    /// <summary>Percent improvement in glucose after site change</summary>
    [JsonPropertyName("percentImprovement")]
    public double PercentImprovement { get; set; }

    /// <summary>Time in range before site change (%)</summary>
    [JsonPropertyName("timeInRangeBeforeChange")]
    public double TimeInRangeBeforeChange { get; set; }

    /// <summary>Time in range after site change (%)</summary>
    [JsonPropertyName("timeInRangeAfterChange")]
    public double TimeInRangeAfterChange { get; set; }

    /// <summary>Coefficient of variation before site change (%)</summary>
    [JsonPropertyName("cvBeforeChange")]
    public double CvBeforeChange { get; set; }

    /// <summary>Coefficient of variation after site change (%)</summary>
    [JsonPropertyName("cvAfterChange")]
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
    /// Total basal insulin in units for the period
    /// </summary>
    public double TotalBasal { get; set; }

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
/// Complete site change impact analysis result
/// </summary>
public class SiteChangeImpactAnalysis
{
    /// <summary>Number of site changes analyzed</summary>
    [JsonPropertyName("siteChangeCount")]
    public int SiteChangeCount { get; set; }

    /// <summary>Time points with averaged glucose data</summary>
    [JsonPropertyName("dataPoints")]
    public List<SiteChangeImpactDataPoint> DataPoints { get; set; } = new();

    /// <summary>Summary statistics comparing before vs after</summary>
    [JsonPropertyName("summary")]
    public SiteChangeImpactSummary Summary { get; set; } = new();

    /// <summary>Hours analyzed before site change</summary>
    [JsonPropertyName("hoursBeforeChange")]
    public int HoursBeforeChange { get; set; }

    /// <summary>Hours analyzed after site change</summary>
    [JsonPropertyName("hoursAfterChange")]
    public int HoursAfterChange { get; set; }

    /// <summary>Bucket size in minutes for averaging</summary>
    [JsonPropertyName("bucketSizeMinutes")]
    public int BucketSizeMinutes { get; set; }

    /// <summary>Whether sufficient data exists for meaningful analysis</summary>
    [JsonPropertyName("hasSufficientData")]
    public bool HasSufficientData { get; set; }
}
