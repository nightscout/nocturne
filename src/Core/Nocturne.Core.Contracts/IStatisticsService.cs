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
    GlycemicVariability CalculateGlycemicVariability(
        IEnumerable<double> values,
        IEnumerable<Entry> entries
    );
    double CalculateEstimatedA1C(double averageGlucose);
    double CalculateMAGE(IEnumerable<double> values);
    double CalculateCONGA(IEnumerable<double> values, int hours = 2);
    double CalculateADRR(IEnumerable<double> values);
    double CalculateLabilityIndex(IEnumerable<Entry> entries);
    double CalculateJIndex(IEnumerable<double> values, double mean);
    double CalculateHBGI(IEnumerable<double> values);
    double CalculateLBGI(IEnumerable<double> values);
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
}
