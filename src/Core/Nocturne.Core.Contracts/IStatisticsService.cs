using Nocturne.Core.Models;

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
    IEnumerable<double> ExtractGlucoseValues(IEnumerable<Entry> entries);

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
        IEnumerable<Entry> entries,
        int days = 14,
        int expectedReadingsPerDay = 288
    );

    /// <summary>
    /// Calculate extended glucose analytics including GMI, GRI, and clinical assessment
    /// </summary>
    ExtendedGlucoseAnalytics AnalyzeGlucoseDataExtended(
        IEnumerable<Entry> entries,
        IEnumerable<Treatment> treatments,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        ExtendedAnalysisConfig? config = null
    );

    // Glycemic Variability

    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 data points.</exception>
    GlycemicVariability CalculateGlycemicVariability(
        IEnumerable<double> values,
        IEnumerable<Entry> entries
    );
    double CalculateEstimatedA1C(double averageGlucose);
    double CalculateMAGE(IEnumerable<double> values);
    double CalculateCONGA(IEnumerable<double> values, int hours = 2);
    double CalculateADRR(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 entries.</exception>
    double CalculateLabilityIndex(IEnumerable<Entry> entries);
    double CalculateJIndex(IEnumerable<double> values, double mean);
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateHBGI(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when the values collection is empty.</exception>
    double CalculateLBGI(IEnumerable<double> values);
    /// <exception cref="ArgumentException">Thrown when there are fewer than 2 values or entries.</exception>
    double CalculateGVI(IEnumerable<double> values, IEnumerable<Entry> entries);
    double CalculatePGS(IEnumerable<double> values, double gvi, double meanGlucose);

    // Time in Range
    TimeInRangeMetrics CalculateTimeInRange(
        IEnumerable<Entry> entries,
        GlycemicThresholds? thresholds = null
    );

    // Glucose Distribution
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistribution(
        IEnumerable<Entry> entries,
        IEnumerable<DistributionBin>? bins = null
    );
    IEnumerable<DistributionDataPoint> CalculateGlucoseDistributionFromValues(
        IEnumerable<double> glucoseValues,
        IEnumerable<DistributionBin>? bins = null
    );
    string CalculateEstimatedHbA1C(IEnumerable<double> values);
    IEnumerable<AveragedStats> CalculateAveragedStats(IEnumerable<Entry> entries);

    // Treatment Statistics
    TreatmentSummary CalculateTreatmentSummary(IEnumerable<Treatment> treatments);
    OverallAverages? CalculateOverallAverages(IEnumerable<DayData> dailyDataPoints);
    double GetTotalInsulin(TreatmentSummary treatmentSummary);
    double GetBolusPercentage(TreatmentSummary treatmentSummary);
    double GetBasalPercentage(TreatmentSummary treatmentSummary);
    bool IsBolusTreatment(Treatment treatment);

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics for a date range
    /// </summary>
    /// <param name="treatments">Collection of treatments to analyze</param>
    /// <param name="startDate">Start of the analysis period</param>
    /// <param name="endDate">End of the analysis period</param>
    /// <returns>Comprehensive insulin delivery statistics</returns>
    InsulinDeliveryStatistics CalculateInsulinDeliveryStatistics(
        IEnumerable<Treatment> treatments,
        DateTime startDate,
        DateTime endDate
    );

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics using StateSpans for basal data
    /// </summary>
    InsulinDeliveryStatistics CalculateInsulinDeliveryStatistics(
        IEnumerable<Treatment> treatments,
        IEnumerable<StateSpan> basalStateSpans,
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
        IEnumerable<Entry> entries,
        IEnumerable<Treatment> treatments,
        ExtendedAnalysisConfig? config = null
    );

    // Site Change Analysis

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="entries">Glucose entries</param>
    /// <param name="treatments">Treatments including site changes</param>
    /// <param name="hoursBeforeChange">Hours before site change to analyze (default: 12)</param>
    /// <param name="hoursAfterChange">Hours after site change to analyze (default: 24)</param>
    /// <param name="bucketSizeMinutes">Time bucket size for averaging (default: 30)</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    SiteChangeImpactAnalysis CalculateSiteChangeImpact(
        IEnumerable<Entry> entries,
        IEnumerable<Treatment> treatments,
        int hoursBeforeChange = 12,
        int hoursAfterChange = 24,
        int bucketSizeMinutes = 30
    );

    /// <summary>
    /// Calculate daily basal/bolus ratio breakdown for chart rendering
    /// </summary>
    /// <param name="treatments">Collection of treatments to analyze (for bolus data)</param>
    /// <returns>Daily breakdown with averages and summary statistics</returns>
    DailyBasalBolusRatioResponse CalculateDailyBasalBolusRatios(IEnumerable<Treatment> treatments);

    /// <summary>
    /// Calculate daily basal/bolus ratio breakdown using StateSpans for basal data
    /// </summary>
    /// <param name="treatments">Collection of treatments to analyze (for bolus data)</param>
    /// <param name="basalStateSpans">Collection of BasalDelivery StateSpans for basal data</param>
    /// <returns>Daily breakdown with averages and summary statistics</returns>
    DailyBasalBolusRatioResponse CalculateDailyBasalBolusRatios(
        IEnumerable<Treatment> treatments,
        IEnumerable<StateSpan> basalStateSpans);

    /// <summary>
    /// Calculate comprehensive basal analysis statistics including percentiles
    /// </summary>
    /// <param name="treatments">Collection of treatments to analyze</param>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <returns>Comprehensive basal analysis with stats, temp basal info, and hourly percentiles</returns>
    BasalAnalysisResponse CalculateBasalAnalysis(IEnumerable<Treatment> treatments, DateTime startDate, DateTime endDate);

    /// <summary>
    /// Calculate comprehensive basal analysis statistics using StateSpans
    /// </summary>
    /// <param name="basalStateSpans">Collection of BasalDelivery StateSpans</param>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <returns>Comprehensive basal analysis with stats, temp basal info, and hourly percentiles</returns>
    BasalAnalysisResponse CalculateBasalAnalysis(
        IEnumerable<StateSpan> basalStateSpans,
        DateTime startDate,
        DateTime endDate);
}
