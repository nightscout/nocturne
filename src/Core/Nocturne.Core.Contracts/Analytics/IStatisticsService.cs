using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service for comprehensive glucose and treatment statistics calculations.
/// Based on International Consensus on Time in Range (2019) and subsequent updates.
/// </summary>
/// <seealso cref="GlucoseManagementIndicator"/>
/// <seealso cref="GlucoseAnalytics"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="GlycemicRiskIndex"/>
public interface IStatisticsService
{
    // Basic Statistics

    /// <summary>
    /// Calculate basic statistics (mean, median, standard deviation, etc.) from raw glucose values.
    /// </summary>
    /// <param name="glucoseValues">Glucose values in mg/dL.</param>
    /// <returns>A <see cref="BasicGlucoseStats"/> containing computed statistics.</returns>
    BasicGlucoseStats CalculateBasicStats(IEnumerable<double> glucoseValues);

    /// <summary>
    /// Calculate the arithmetic mean of a collection of values.
    /// </summary>
    /// <param name="values">Numeric values to average.</param>
    /// <returns>Arithmetic mean.</returns>
    double CalculateMean(IEnumerable<double> values);

    /// <summary>
    /// Calculate a specific percentile from pre-sorted values.
    /// </summary>
    /// <param name="sortedValues">Values sorted in ascending order.</param>
    /// <param name="percentile">Percentile to calculate (0-100).</param>
    /// <returns>The value at the requested percentile.</returns>
    double CalculatePercentile(IEnumerable<double> sortedValues, double percentile);

    /// <summary>
    /// Extract glucose values in mg/dL from <see cref="SensorGlucose"/> entries.
    /// </summary>
    /// <param name="entries"><see cref="SensorGlucose"/> entries to extract values from.</param>
    /// <returns>Glucose values in mg/dL.</returns>
    IEnumerable<double> ExtractGlucoseValues(IEnumerable<SensorGlucose> entries);

    // Modern Glycemic Indicators

    /// <summary>
    /// Calculate Glucose Management Indicator (GMI) - modern replacement for estimated A1c
    /// Formula: GMI (%) = 3.31 + (0.02392 × mean glucose in mg/dL)
    /// </summary>
    GlucoseManagementIndicator CalculateGMI(double meanGlucose);

    /// <summary>
    /// Calculate Glycemic Risk Index (GRI) - composite risk score from 0-100
    /// Formula: GRI = (3.0 × VLow%) + (2.4 × Low%) + (1.6 × VHigh%) + (0.8 × High%)
    /// </summary>
    GlycemicRiskIndex CalculateGRI(TimeInRangeMetrics timeInRange);

    /// <summary>
    /// Assess glucose data against clinical targets for a specific diabetes population
    /// </summary>
    ClinicalTargetAssessment AssessAgainstTargets(
        GlucoseAnalytics analytics,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult
    );

    /// <summary>
    /// Check if there is sufficient data for a valid clinical report
    /// Requires minimum 70% data coverage per international guidelines
    /// </summary>
    DataSufficiencyAssessment AssessDataSufficiency(
        IEnumerable<SensorGlucose> entries,
        int days = 14,
        int expectedReadingsPerDay = 288
    );

    /// <summary>
    /// Assess the reliability of a statistics block based on data duration and completeness.
    /// Returns raw facts (days of data, reading count, recommended minimum) so the frontend
    /// can compose a plain-English reliability message.
    /// </summary>
    StatisticReliability AssessReliability(
        int daysOfData,
        int readingCount,
        int recommendedMinimumDays = 14
    );

    /// <summary>
    /// Calculate extended glucose analytics including GMI, GRI, and clinical assessment
    /// </summary>
    ExtendedGlucoseAnalytics AnalyzeGlucoseDataExtended(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        ExtendedAnalysisConfig? config = null
    );

    // Glycemic Variability

    /// <summary>
    /// Calculate comprehensive glycemic variability metrics.
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <param name="entries"><see cref="SensorGlucose"/> entries with timestamps for time-dependent metrics.</param>
    /// <returns>A <see cref="GlycemicVariability"/> containing all variability metrics.</returns>
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 data points.</exception>
    GlycemicVariability CalculateGlycemicVariability(
        IEnumerable<double> values,
        IEnumerable<SensorGlucose> entries
    );

    /// <summary>
    /// Calculate estimated A1C from average glucose using the Nathan formula.
    /// </summary>
    /// <param name="averageGlucose">Average glucose in mg/dL.</param>
    /// <returns>Estimated A1C as a percentage.</returns>
    double CalculateEstimatedA1C(double averageGlucose);

    /// <summary>
    /// Calculate Mean Amplitude of Glycemic Excursions (MAGE).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <returns>MAGE value in mg/dL.</returns>
    double CalculateMAGE(IEnumerable<double> values);

    /// <summary>
    /// Calculate Continuous Overall Net Glycemic Action (CONGA).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL at regular intervals.</param>
    /// <param name="hours">CONGA window in hours (default 2).</param>
    /// <returns>CONGA value.</returns>
    double CalculateCONGA(IEnumerable<double> values, int hours = 2);

    /// <summary>
    /// Calculate Average Daily Risk Range (ADRR).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <returns>ADRR value.</returns>
    double CalculateADRR(IEnumerable<double> values);

    /// <summary>
    /// Calculate the Lability Index from timestamped glucose entries.
    /// </summary>
    /// <param name="entries"><see cref="SensorGlucose"/> entries.</param>
    /// <returns>Lability Index value.</returns>
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 entries.</exception>
    double CalculateLabilityIndex(IEnumerable<SensorGlucose> entries);

    /// <summary>
    /// Calculate the J-Index (combines mean and variability).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <param name="mean">Pre-calculated mean glucose in mg/dL.</param>
    /// <returns>J-Index value.</returns>
    double CalculateJIndex(IEnumerable<double> values, double mean);

    /// <summary>
    /// Calculate High Blood Glucose Index (HBGI).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <returns>HBGI value.</returns>
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateHBGI(IEnumerable<double> values);

    /// <summary>
    /// Calculate Low Blood Glucose Index (LBGI).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <returns>LBGI value.</returns>
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateLBGI(IEnumerable<double> values);

    /// <summary>
    /// Calculate Glycemic Variability Index (GVI).
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <param name="entries"><see cref="SensorGlucose"/> entries with timestamps.</param>
    /// <returns>GVI value (1.0 = perfectly stable).</returns>
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 values or entries.</exception>
    double CalculateGVI(IEnumerable<double> values, IEnumerable<SensorGlucose> entries);

    /// <summary>
    /// Calculate Patient Glycemic Status (PGS) composite score.
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <param name="gvi">Pre-calculated GVI.</param>
    /// <param name="meanGlucose">Pre-calculated mean glucose in mg/dL.</param>
    /// <returns>PGS composite score.</returns>
    double CalculatePGS(IEnumerable<double> values, double gvi, double meanGlucose);

    // Time in Range

    /// <summary>
    /// Calculate Time in Range (TIR) metrics per international consensus thresholds.
    /// </summary>
    /// <param name="entries"><see cref="SensorGlucose"/> entries.</param>
    /// <param name="thresholds">Optional custom <see cref="GlycemicThresholds"/>. Uses consensus defaults if <c>null</c>.</param>
    /// <returns><see cref="TimeInRangeMetrics"/> with percentages for each range.</returns>
    TimeInRangeMetrics CalculateTimeInRange(
        IEnumerable<SensorGlucose> entries,
        GlycemicThresholds? thresholds = null
    );

    // Glucose Distribution

    /// <summary>
    /// Calculate glucose distribution across bins from <see cref="SensorGlucose"/> entries.
    /// </summary>
    /// <param name="entries"><see cref="SensorGlucose"/> entries.</param>
    /// <param name="bins">Optional custom distribution bins. Uses default bins if <c>null</c>.</param>
    /// <returns>Distribution data points for histogram rendering.</returns>
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistribution(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<DistributionBin>? bins = null
    );

    /// <summary>
    /// Calculate glucose distribution across bins from raw glucose values.
    /// </summary>
    /// <param name="glucoseValues">Glucose values in mg/dL.</param>
    /// <param name="bins">Optional custom distribution bins. Uses default bins if <c>null</c>.</param>
    /// <returns>Distribution data points for histogram rendering.</returns>
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistributionFromValues(
        IEnumerable<double> glucoseValues,
        IEnumerable<DistributionBin>? bins = null
    );

    /// <summary>
    /// Calculate estimated HbA1c as a formatted string from glucose values.
    /// </summary>
    /// <param name="values">Glucose values in mg/dL.</param>
    /// <returns>Formatted estimated HbA1c string (e.g., "6.5%").</returns>
    string CalculateEstimatedHbA1C(IEnumerable<double> values);

    /// <summary>
    /// Calculate averaged statistics bucketed by time of day from <see cref="SensorGlucose"/> entries.
    /// </summary>
    /// <param name="entries"><see cref="SensorGlucose"/> entries.</param>
    /// <returns>Time-of-day averaged statistics for AGP-style charts.</returns>
    IEnumerable<AveragedStats> CalculateAveragedStats(IEnumerable<SensorGlucose> entries);

    // Treatment Statistics

    /// <summary>
    /// Calculate a treatment summary from <see cref="Bolus"/> and <see cref="CarbIntake"/> records.
    /// </summary>
    /// <param name="boluses"><see cref="Bolus"/> records.</param>
    /// <param name="carbIntakes"><see cref="CarbIntake"/> records.</param>
    /// <param name="foodsByCarbIntake">Optional food breakdown keyed by <see cref="CarbIntake"/> ID.</param>
    /// <returns>A <see cref="TreatmentSummary"/> with insulin and carb statistics.</returns>
    TreatmentSummary CalculateTreatmentSummary(IEnumerable<Bolus> boluses, IEnumerable<CarbIntake> carbIntakes, IReadOnlyDictionary<Guid, List<TreatmentFood>>? foodsByCarbIntake = null);

    /// <summary>
    /// Calculate overall daily averages from per-day data points.
    /// </summary>
    /// <param name="dailyDataPoints">Per-day data points.</param>
    /// <returns>Overall averages, or <c>null</c> if no data.</returns>
    OverallAverages? CalculateOverallAverages(IEnumerable<DayData> dailyDataPoints);

    /// <summary>
    /// Get total daily insulin from a <see cref="TreatmentSummary"/>.
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary to read from.</param>
    /// <returns>Total insulin in units.</returns>
    double GetTotalInsulin(TreatmentSummary treatmentSummary);

    /// <summary>
    /// Get bolus insulin as a percentage of total daily insulin.
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary to read from.</param>
    /// <returns>Bolus percentage (0-100).</returns>
    double GetBolusPercentage(TreatmentSummary treatmentSummary);

    /// <summary>
    /// Get basal insulin as a percentage of total daily insulin.
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary to read from.</param>
    /// <returns>Basal percentage (0-100).</returns>
    double GetBasalPercentage(TreatmentSummary treatmentSummary);

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics.
    /// Basal data comes from TempBasals and algorithmBoluses; pass empty collections if none are available.
    /// </summary>
    InsulinDeliveryStatistics CalculateInsulinDeliveryStatistics(
        IEnumerable<Bolus> boluses,
        IEnumerable<Bolus> algorithmBoluses,
        IEnumerable<TempBasal> tempBasals,
        IEnumerable<CarbIntake> carbIntakes,
        DateTime startDate,
        DateTime endDate
    );

    // Formatting Utilities

    /// <summary>Format an insulin value for display (e.g., "1.25U").</summary>
    /// <param name="value">Insulin value in units.</param>
    /// <returns>Formatted display string.</returns>
    string FormatInsulinDisplay(double value);

    /// <summary>Format a carb value for display (e.g., "45g").</summary>
    /// <param name="value">Carb value in grams.</param>
    /// <returns>Formatted display string.</returns>
    string FormatCarbDisplay(double value);

    /// <summary>Format a percentage value for display.</summary>
    /// <param name="value">Percentage value (0-100).</param>
    /// <returns>Formatted display string.</returns>
    string FormatPercentageDisplay(double value);

    /// <summary>Round an insulin value to pump delivery precision.</summary>
    /// <param name="value">Insulin value in units.</param>
    /// <param name="step">Pump step size in units (default 0.05).</param>
    /// <returns>Rounded insulin value.</returns>
    double RoundInsulinToPumpPrecision(double value, double step = 0.05);

    // Validation

    /// <summary>Validate that a <see cref="Treatment"/> has the required data for statistics.</summary>
    /// <param name="treatment"><see cref="Treatment"/> to validate.</param>
    /// <returns><c>true</c> if the treatment has valid data for statistics.</returns>
    bool ValidateTreatmentData(Treatment treatment);

    /// <summary>Filter and clean <see cref="Treatment"/> records, removing invalid entries.</summary>
    /// <param name="treatments"><see cref="Treatment"/> records to clean.</param>
    /// <returns>Cleaned treatment records.</returns>
    IEnumerable<Treatment> CleanTreatmentData(IEnumerable<Treatment> treatments);

    // Unit Conversions

    /// <summary>Convert a glucose value from mg/dL to mmol/L.</summary>
    /// <param name="mgdl">Glucose value in mg/dL.</param>
    /// <returns>Glucose value in mmol/L.</returns>
    double MgdlToMMOL(double mgdl);

    /// <summary>Convert a glucose value from mmol/L to mg/dL.</summary>
    /// <param name="mmol">Glucose value in mmol/L.</param>
    /// <returns>Glucose value in mg/dL.</returns>
    double MmolToMGDL(double mmol);

    /// <summary>Convert a glucose value from mg/dL to a formatted mmol/L string.</summary>
    /// <param name="mgdl">Glucose value in mg/dL.</param>
    /// <returns>Formatted mmol/L string.</returns>
    string MgdlToMMOLString(double mgdl);

    // Comprehensive Analytics
    GlucoseAnalytics AnalyzeGlucoseData(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        ExtendedAnalysisConfig? config = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int updateIntervalMinutes = 5
    );

    // Site Change Analysis

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="entries">Glucose entries</param>
    /// <param name="deviceEvents">Device events including site changes</param>
    /// <param name="hoursBeforeChange">Hours before site change to analyze (default: 12)</param>
    /// <param name="hoursAfterChange">Hours after site change to analyze (default: 24)</param>
    /// <param name="bucketSizeMinutes">Time bucket size for averaging (default: 30)</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    SiteChangeImpactAnalysis CalculateSiteChangeImpact(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<DeviceEvent> deviceEvents,
        int hoursBeforeChange = 12,
        int hoursAfterChange = 24,
        int bucketSizeMinutes = 30
    );

    /// <summary>
    /// Calculate daily basal/bolus ratio breakdown.
    /// Basal data comes from TempBasals and algorithm boluses; pass empty collections if none are available.
    /// </summary>
    DailyBasalBolusRatioResponse CalculateDailyBasalBolusRatios(
        IEnumerable<Bolus> boluses,
        IEnumerable<Bolus> algorithmBoluses,
        IEnumerable<TempBasal> tempBasals,
        TimeZoneInfo? userTimeZone = null);

    /// <summary>
    /// Calculate comprehensive basal analysis statistics from TempBasals.
    /// Pass empty collections if none are available.
    /// </summary>
    BasalAnalysisResponse CalculateBasalAnalysis(
        IEnumerable<TempBasal> tempBasals,
        IEnumerable<Bolus> algorithmBoluses,
        DateTime startDate,
        DateTime endDate);
}
