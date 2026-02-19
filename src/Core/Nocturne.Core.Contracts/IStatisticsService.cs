using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Interface for comprehensive glucose and treatment statistics calculations
/// Based on International Consensus on Time in Range (2019) and subsequent updates
/// </summary>
public interface IStatisticsService
{
    // Basic Statistics
    BasicGlucoseStats CalculateBasicStats(IEnumerable<double> glucoseValues);
    double CalculateMean(IEnumerable<double> values);
    double CalculatePercentile(IEnumerable<double> sortedValues, double percentile);
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

    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 data points.</exception>
    GlycemicVariability CalculateGlycemicVariability(
        IEnumerable<double> values,
        IEnumerable<SensorGlucose> entries
    );
    double CalculateEstimatedA1C(double averageGlucose);
    double CalculateMAGE(IEnumerable<double> values);
    double CalculateCONGA(IEnumerable<double> values, int hours = 2);
    double CalculateADRR(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 entries.</exception>
    double CalculateLabilityIndex(IEnumerable<SensorGlucose> entries);
    double CalculateJIndex(IEnumerable<double> values, double mean);
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateHBGI(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateLBGI(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 values or entries.</exception>
    double CalculateGVI(IEnumerable<double> values, IEnumerable<SensorGlucose> entries);
    double CalculatePGS(IEnumerable<double> values, double gvi, double meanGlucose);

    // Time in Range
    TimeInRangeMetrics CalculateTimeInRange(
        IEnumerable<SensorGlucose> entries,
        GlycemicThresholds? thresholds = null
    );

    // Glucose Distribution
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistribution(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<DistributionBin>? bins = null
    );
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistributionFromValues(
        IEnumerable<double> glucoseValues,
        IEnumerable<DistributionBin>? bins = null
    );
    string CalculateEstimatedHbA1C(IEnumerable<double> values);
    IEnumerable<AveragedStats> CalculateAveragedStats(IEnumerable<SensorGlucose> entries);

    // Treatment Statistics
    TreatmentSummary CalculateTreatmentSummary(IEnumerable<Bolus> boluses, IEnumerable<CarbIntake> carbIntakes);
    OverallAverages? CalculateOverallAverages(IEnumerable<DayData> dailyDataPoints);
    double GetTotalInsulin(TreatmentSummary treatmentSummary);
    double GetBolusPercentage(TreatmentSummary treatmentSummary);
    double GetBasalPercentage(TreatmentSummary treatmentSummary);

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics.
    /// Basal data comes from StateSpans; pass an empty collection if none are available.
    /// </summary>
    InsulinDeliveryStatistics CalculateInsulinDeliveryStatistics(
        IEnumerable<Bolus> boluses,
        IEnumerable<StateSpan> basalStateSpans,
        IEnumerable<CarbIntake> carbIntakes,
        DateTime startDate,
        DateTime endDate
    );

    /// <summary>
    /// Sum total basal insulin delivered across a collection of BasalDelivery StateSpans
    /// </summary>
    double SumBasalFromStateSpans(IEnumerable<StateSpan> basalStateSpans);

    // Formatting Utilities
    string FormatInsulinDisplay(double value);
    string FormatCarbDisplay(double value);
    string FormatPercentageDisplay(double value);
    double RoundInsulinToPumpPrecision(double value, double step = 0.05);

    // Validation
    bool ValidateTreatmentData(Treatment treatment);
    IEnumerable<Treatment> CleanTreatmentData(IEnumerable<Treatment> treatments);

    // Unit Conversions
    double MgdlToMMOL(double mgdl);
    double MmolToMGDL(double mmol);
    string MgdlToMMOLString(double mgdl);

    // Comprehensive Analytics
    GlucoseAnalytics AnalyzeGlucoseData(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        ExtendedAnalysisConfig? config = null
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
    /// Basal data comes from StateSpans; pass an empty collection if none are available.
    /// </summary>
    DailyBasalBolusRatioResponse CalculateDailyBasalBolusRatios(
        IEnumerable<Bolus> boluses,
        IEnumerable<StateSpan> basalStateSpans);

    /// <summary>
    /// Calculate comprehensive basal analysis statistics from StateSpans.
    /// Pass an empty collection if none are available.
    /// </summary>
    BasalAnalysisResponse CalculateBasalAnalysis(
        IEnumerable<StateSpan> basalStateSpans,
        DateTime startDate,
        DateTime endDate);
}
