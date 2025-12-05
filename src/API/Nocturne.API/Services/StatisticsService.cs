using System.Text.RegularExpressions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Comprehensive glucose and treatment statistics calculations service
/// Provides 1:1 functionality with the TypeScript utilities for complete API parity
/// </summary>
public class StatisticsService : IStatisticsService
{
    private static readonly string[] BolusTreatmentTypes = new[]
    {
        "Meal Bolus",
        "Correction Bolus",
        "Snack Bolus",
        "Bolus Wizard",
        "Combo Bolus",
    };

    private static readonly IEnumerable<DistributionBin> DefaultDistributionBins = new[]
    {
        new DistributionBin
        {
            Range = "<40",
            Min = 0,
            Max = 39,
        },
        new DistributionBin
        {
            Range = "40-50",
            Min = 40,
            Max = 50,
        },
        new DistributionBin
        {
            Range = "50-60",
            Min = 51,
            Max = 60,
        },
        new DistributionBin
        {
            Range = "60-70",
            Min = 61,
            Max = 70,
        },
        new DistributionBin
        {
            Range = "70-80",
            Min = 71,
            Max = 80,
        },
        new DistributionBin
        {
            Range = "80-90",
            Min = 81,
            Max = 90,
        },
        new DistributionBin
        {
            Range = "90-100",
            Min = 91,
            Max = 100,
        },
        new DistributionBin
        {
            Range = "100-110",
            Min = 101,
            Max = 110,
        },
        new DistributionBin
        {
            Range = "110-120",
            Min = 111,
            Max = 120,
        },
        new DistributionBin
        {
            Range = "120-130",
            Min = 121,
            Max = 130,
        },
        new DistributionBin
        {
            Range = "130-140",
            Min = 131,
            Max = 140,
        },
        new DistributionBin
        {
            Range = "140-150",
            Min = 141,
            Max = 150,
        },
        new DistributionBin
        {
            Range = "150-180",
            Min = 151,
            Max = 180,
        },
        new DistributionBin
        {
            Range = "180-250",
            Min = 181,
            Max = 250,
        },
        new DistributionBin
        {
            Range = "250-300",
            Min = 251,
            Max = 300,
        },
        new DistributionBin
        {
            Range = ">300",
            Min = 301,
            Max = 9999,
        },
    };

    #region Modern Glycemic Indicators

    /// <summary>
    /// Calculate Glucose Management Indicator (GMI) - modern replacement for estimated A1c
    /// Based on: Bergenstal RM, et al. Diabetes Care. 2018
    /// Formula: GMI (%) = 3.31 + (0.02392 × mean glucose in mg/dL)
    /// </summary>
    /// <param name="meanGlucose">Mean glucose in mg/dL</param>
    /// <returns>GMI with value, interpretation, and source mean glucose</returns>
    public GlucoseManagementIndicator CalculateGMI(double meanGlucose)
    {
        if (meanGlucose <= 0)
        {
            return new GlucoseManagementIndicator
            {
                Value = 0,
                MeanGlucose = 0,
                Interpretation = "Insufficient data",
            };
        }

        // GMI formula: 3.31 + (0.02392 × mean glucose in mg/dL)
        var gmiValue = 3.31 + (0.02392 * meanGlucose);
        gmiValue = Math.Round(gmiValue * 10) / 10; // Round to 1 decimal

        return new GlucoseManagementIndicator
        {
            Value = gmiValue,
            MeanGlucose = meanGlucose,
            Interpretation = GlucoseManagementIndicator.GetInterpretation(gmiValue),
        };
    }

    /// <summary>
    /// Calculate Glycemic Risk Index (GRI) - composite risk score from 0-100
    /// Based on: Klonoff DC, et al. J Diabetes Sci Technol. 2023
    /// Formula: GRI = (3.0 × VLow%) + (2.4 × Low%) + (1.6 × VHigh%) + (0.8 × High%)
    /// </summary>
    /// <param name="timeInRange">Time in range metrics with percentage breakdowns</param>
    /// <returns>GRI with score, zone classification, and component breakdown</returns>
    public GlycemicRiskIndex CalculateGRI(TimeInRangeMetrics timeInRange)
    {
        var percentages = timeInRange.Percentages;

        // GRI component weights per 2023 consensus
        const double veryLowWeight = 3.0;
        const double lowWeight = 2.4;
        const double highWeight = 0.8;
        const double veryHighWeight = 1.6;

        var hypoComponent = (veryLowWeight * percentages.SevereLow) + (lowWeight * percentages.Low);
        var hyperComponent =
            (veryHighWeight * percentages.SevereHigh) + (highWeight * percentages.High);

        var gri = hypoComponent + hyperComponent;

        // Cap at 100
        gri = Math.Min(100, Math.Round(gri * 10) / 10);

        var zone = gri switch
        {
            <= 20 => GRIZone.A,
            <= 40 => GRIZone.B,
            <= 60 => GRIZone.C,
            <= 80 => GRIZone.D,
            _ => GRIZone.E,
        };

        var interpretation = zone switch
        {
            GRIZone.A => "Excellent glycemic control - lowest risk",
            GRIZone.B => "Good glycemic control - low risk",
            GRIZone.C => "Moderate glycemic control - moderate risk",
            GRIZone.D => "Suboptimal glycemic control - high risk",
            GRIZone.E => "Poor glycemic control - very high risk",
            _ => "Unknown",
        };

        return new GlycemicRiskIndex
        {
            Score = gri,
            HypoglycemiaComponent = Math.Round(hypoComponent * 10) / 10,
            HyperglycemiaComponent = Math.Round(hyperComponent * 10) / 10,
            Zone = zone,
            Interpretation = interpretation,
        };
    }

    /// <summary>
    /// Assess glucose data against clinical targets for a specific diabetes population
    /// Based on International Consensus on Time in Range (2019) and subsequent updates
    /// </summary>
    public ClinicalTargetAssessment AssessAgainstTargets(
        GlucoseAnalytics analytics,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult
    )
    {
        var targets = ClinicalTargets.ForPopulation(population);
        var tir = analytics.TimeInRange.Percentages;
        var cv = analytics.GlycemicVariability.CoefficientOfVariation;

        var assessment = new ClinicalTargetAssessment
        {
            Population = population,
            Targets = targets,
            TotalTargets = 6,
        };

        // Time in Range Assessment (minimum target)
        assessment.TIRAssessment = AssessMinimumTarget(
            "Time in Range",
            tir.Target,
            targets.TargetTIR
        );

        // Time Below Range Assessment (maximum target - lower is better)
        var totalTBR = tir.SevereLow + tir.Low;
        assessment.TBRAssessment = AssessMaximumTarget(
            "Time Below Range",
            totalTBR,
            targets.MaxTBR
        );

        // Very Low Assessment (maximum target)
        assessment.VeryLowAssessment = AssessMaximumTarget(
            "Time Very Low (<54)",
            tir.SevereLow,
            targets.MaxTBRVeryLow
        );

        // Time Above Range Assessment (maximum target)
        var totalTAR = tir.SevereHigh + tir.High;
        assessment.TARAssessment = AssessMaximumTarget(
            "Time Above Range",
            totalTAR,
            targets.MaxTAR
        );

        // Very High Assessment (maximum target)
        assessment.VeryHighAssessment = AssessMaximumTarget(
            "Time Very High (>250)",
            tir.SevereHigh,
            targets.MaxTARVeryHigh
        );

        // CV Assessment (maximum target)
        assessment.CVAssessment = AssessMaximumTarget(
            "Coefficient of Variation",
            cv,
            targets.TargetCV
        );

        // Count targets met
        var assessments = new[]
        {
            assessment.TIRAssessment,
            assessment.TBRAssessment,
            assessment.VeryLowAssessment,
            assessment.TARAssessment,
            assessment.VeryHighAssessment,
            assessment.CVAssessment,
        };

        assessment.TargetsMet = assessments.Count(a => a.Status == TargetStatus.Met);

        // Determine overall assessment
        assessment.OverallAssessment = assessment.TargetsMet switch
        {
            6 => "Excellent",
            >= 4 => "Good",
            >= 2 => "Needs Attention",
            _ => "Needs Significant Improvement",
        };

        // Generate actionable insights
        GenerateActionableInsights(assessment, tir, cv, targets);

        return assessment;
    }

    private TargetAssessment AssessMinimumTarget(string name, double current, double target)
    {
        var status =
            current >= target ? TargetStatus.Met
            : current >= target * 0.9 ? TargetStatus.Close
            : TargetStatus.NotMet;

        return new TargetAssessment
        {
            MetricName = name,
            CurrentValue = Math.Round(current * 10) / 10,
            TargetValue = target,
            IsMaximumTarget = false,
            Status = status,
            DifferenceFromTarget = Math.Round((current - target) * 10) / 10,
            ProgressPercentage =
                target > 0 ? Math.Min(100, Math.Round(current / target * 100 * 10) / 10) : 0,
        };
    }

    private TargetAssessment AssessMaximumTarget(string name, double current, double target)
    {
        var status =
            current <= target ? TargetStatus.Met
            : current <= target * 1.1 ? TargetStatus.Close
            : TargetStatus.NotMet;

        return new TargetAssessment
        {
            MetricName = name,
            CurrentValue = Math.Round(current * 10) / 10,
            TargetValue = target,
            IsMaximumTarget = true,
            Status = status,
            DifferenceFromTarget = Math.Round((current - target) * 10) / 10,
            ProgressPercentage =
                target > 0
                    ? Math.Max(0, Math.Round((1 - (current - target) / target) * 100 * 10) / 10)
                    : (current == 0 ? 100 : 0),
        };
    }

    private void GenerateActionableInsights(
        ClinicalTargetAssessment assessment,
        TimeInRangePercentages tir,
        double cv,
        ClinicalTargets targets
    )
    {
        // Strengths
        if (assessment.TIRAssessment.Status == TargetStatus.Met)
            assessment.Strengths.Add(
                $"Time in range is excellent at {tir.Target:F1}% (target: ≥{targets.TargetTIR}%)"
            );

        if (assessment.VeryLowAssessment.Status == TargetStatus.Met && tir.SevereLow == 0)
            assessment.Strengths.Add("No severe hypoglycemia detected - great job staying safe!");

        if (assessment.CVAssessment.Status == TargetStatus.Met)
            assessment.Strengths.Add($"Glucose variability is well-controlled at {cv:F1}% CV");

        // Priority areas for improvement
        if (assessment.VeryLowAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add("Reducing severe hypoglycemia (<54 mg/dL)");
            assessment.ActionableInsights.Add(
                $"⚠️ Time very low is {tir.SevereLow:F1}% (target: <{targets.MaxTBRVeryLow}%). Review overnight basal rates and consider CGM alerts."
            );
        }

        if (assessment.TBRAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add("Reducing overall hypoglycemia (<70 mg/dL)");
            assessment.ActionableInsights.Add(
                $"Time below range is {tir.SevereLow + tir.Low:F1}% (target: <{targets.MaxTBR}%). Consider adjusting correction factors or meal timing."
            );
        }

        if (assessment.TIRAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add("Increasing time in target range");
            assessment.ActionableInsights.Add(
                $"Time in range is {tir.Target:F1}% (target: ≥{targets.TargetTIR}%). Focus on post-meal management and consistent timing."
            );
        }

        if (assessment.VeryHighAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add("Reducing severe hyperglycemia (>250 mg/dL)");
            assessment.ActionableInsights.Add(
                $"Time very high is {tir.SevereHigh:F1}% (target: <{targets.MaxTARVeryHigh}%). Review carb counting and correction doses."
            );
        }

        if (assessment.CVAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add("Reducing glucose variability");
            assessment.ActionableInsights.Add(
                $"Glucose variability is {cv:F1}% (target: <{targets.TargetCV}%). Consider more consistent meal timing and composition."
            );
        }

        // If everything is good
        if (assessment.TargetsMet == assessment.TotalTargets)
        {
            assessment.ActionableInsights.Add("🎉 All targets met! Keep up the excellent work.");
        }
    }

    /// <summary>
    /// Check if there is sufficient data for a valid clinical report
    /// Per international guidelines, minimum 70% data coverage is required
    /// </summary>
    public DataSufficiencyAssessment AssessDataSufficiency(
        IEnumerable<Entry> entries,
        int days = 14,
        int expectedReadingsPerDay = 288 // 5-minute intervals = 288/day
    )
    {
        var entriesList = entries.ToList();
        var expectedTotal = days * expectedReadingsPerDay;

        if (!entriesList.Any())
        {
            return new DataSufficiencyAssessment
            {
                IsSufficient = false,
                TotalDays = days,
                DaysWithData = 0,
                ExpectedReadings = expectedTotal,
                ActualReadings = 0,
                CompletenessPercentage = 0,
                WarningMessage = "No glucose data available for the selected period.",
                Recommendation = "Ensure your CGM is connected and uploading data.",
            };
        }

        // Group by date to count days with data
        var entriesByDate = entriesList
            .Where(e => e.Mills > 0 || e.Date.HasValue)
            .GroupBy(e =>
            {
                var dt =
                    e.Mills > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(e.Mills).Date
                        : e.Date?.Date ?? DateTime.MinValue;
                return dt;
            })
            .Where(g => g.Key != DateTime.MinValue)
            .ToList();

        var daysWithData = entriesByDate.Count;
        var actualReadings = entriesList.Count;
        var completeness = expectedTotal > 0 ? (double)actualReadings / expectedTotal * 100 : 0;
        var avgPerDay = daysWithData > 0 ? (double)actualReadings / daysWithData : 0;

        // Calculate longest gap
        var sortedEntries = entriesList.Where(e => e.Mills > 0).OrderBy(e => e.Mills).ToList();

        double longestGapHours = 0;
        if (sortedEntries.Count > 1)
        {
            for (int i = 1; i < sortedEntries.Count; i++)
            {
                var gapMs = sortedEntries[i].Mills - sortedEntries[i - 1].Mills;
                var gapHours = gapMs / (1000.0 * 60 * 60);
                if (gapHours > longestGapHours)
                    longestGapHours = gapHours;
            }
        }

        var isSufficient = completeness >= 70;

        string warningMessage = "";
        string recommendation = "";

        if (!isSufficient)
        {
            warningMessage =
                $"Data coverage is {completeness:F0}%, below the recommended 70% minimum for reliable analysis.";
            recommendation =
                completeness < 50
                    ? "Consider extending the date range or checking sensor connectivity."
                    : "Results may be less reliable. Try to maintain consistent sensor wear.";
        }
        else if (longestGapHours > 12)
        {
            warningMessage = $"A data gap of {longestGapHours:F1} hours was detected.";
            recommendation = "Large gaps may affect the accuracy of pattern analysis.";
        }

        return new DataSufficiencyAssessment
        {
            IsSufficient = isSufficient,
            TotalDays = days,
            DaysWithData = daysWithData,
            ExpectedReadings = expectedTotal,
            ActualReadings = actualReadings,
            CompletenessPercentage = Math.Round(completeness * 10) / 10,
            AverageReadingsPerDay = Math.Round(avgPerDay),
            LongestGapHours = Math.Round(longestGapHours * 10) / 10,
            WarningMessage = warningMessage,
            Recommendation = recommendation,
        };
    }

    /// <summary>
    /// Calculate extended glucose analytics including GMI, GRI, and clinical assessment
    /// </summary>
    public ExtendedGlucoseAnalytics AnalyzeGlucoseDataExtended(
        IEnumerable<Entry> entries,
        IEnumerable<Treatment> treatments,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        ExtendedAnalysisConfig? config = null
    )
    {
        var entriesList = entries.ToList();
        var treatmentsList = treatments.ToList();

        // Get base analytics
        var baseAnalytics = AnalyzeGlucoseData(entriesList, treatmentsList, config);

        // Calculate GMI
        var gmi = CalculateGMI(baseAnalytics.BasicStats.Mean);

        // Calculate GRI
        var gri = CalculateGRI(baseAnalytics.TimeInRange);

        // Assess against clinical targets
        var clinicalAssessment = AssessAgainstTargets(baseAnalytics, population);

        // Assess data sufficiency (default 14 days)
        var dataSufficiency = AssessDataSufficiency(entriesList, 14);

        // Calculate treatment summary if treatments available
        TreatmentSummary? treatmentSummary = null;
        if (treatmentsList.Any())
        {
            treatmentSummary = CalculateTreatmentSummary(treatmentsList);
        }

        return new ExtendedGlucoseAnalytics
        {
            // Base analytics properties
            Time = baseAnalytics.Time,
            BasicStats = baseAnalytics.BasicStats,
            TimeInRange = baseAnalytics.TimeInRange,
            GlycemicVariability = baseAnalytics.GlycemicVariability,
            DataQuality = baseAnalytics.DataQuality,

            // Extended metrics
            GMI = gmi,
            GRI = gri,
            ClinicalAssessment = clinicalAssessment,
            DataSufficiency = dataSufficiency,
            TreatmentSummary = treatmentSummary,
        };
    }

    #endregion

    #region Basic Statistics

    /// <summary>
    /// Calculate basic glucose statistics from glucose values
    /// </summary>
    /// <param name="glucoseValues">Collection of glucose values in mg/dL</param>
    /// <returns>Basic glucose statistics including mean, median, percentiles, etc.</returns>
    public BasicGlucoseStats CalculateBasicStats(IEnumerable<double> glucoseValues)
    {
        var values = glucoseValues.Where(v => v > 0 && v < 600).ToList();

        if (!values.Any())
        {
            return new BasicGlucoseStats
            {
                Count = 0,
                Mean = 0,
                Median = 0,
                Min = 0,
                Max = 0,
                StandardDeviation = 0,
                Percentiles = new GlucosePercentiles(),
            };
        }

        var sorted = values.OrderBy(v => v).ToList();
        var count = values.Count;
        var mean = CalculateMean(values);

        // Calculate median correctly for even/odd number of values
        double median;
        if (count % 2 == 0)
        {
            // Even number of values - average of two middle values
            median = (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        }
        else
        {
            // Odd number of values - middle value
            median = sorted[count / 2];
        }

        var min = sorted[0];
        var max = sorted[count - 1];

        // Standard deviation
        var variance = count > 1 ? values.Sum(v => Math.Pow(v - mean, 2)) / (count - 1) : 0;
        var standardDeviation = Math.Sqrt(variance);

        // Percentiles
        var percentiles = new GlucosePercentiles
        {
            P5 = CalculatePercentile(sorted, 5),
            P10 = CalculatePercentile(sorted, 10),
            P25 = CalculatePercentile(sorted, 25),
            P50 = median,
            P75 = CalculatePercentile(sorted, 75),
            P90 = CalculatePercentile(sorted, 90),
            P95 = CalculatePercentile(sorted, 95),
        };

        return new BasicGlucoseStats
        {
            Count = count,
            Mean = Math.Round(mean * 10) / 10,
            Median = median,
            Min = min,
            Max = max,
            StandardDeviation = Math.Round(standardDeviation * 10) / 10,
            Percentiles = percentiles,
        };
    }

    /// <summary>
    /// Calculate the mean (average) of a collection of values
    /// </summary>
    /// <param name="values">Collection of numeric values</param>
    /// <returns>Mean value rounded to one decimal place</returns>
    public double CalculateMean(IEnumerable<double> values)
    {
        var valuesList = values.ToList();
        if (!valuesList.Any())
            return 0;

        var sum = valuesList.Sum();
        return Math.Round((sum / valuesList.Count) * 10) / 10;
    }

    /// <summary>
    /// Calculate a specific percentile from a sorted array of values
    /// </summary>
    /// <param name="sortedValues">Pre-sorted collection of values</param>
    /// <param name="percentile">Percentile to calculate (0-100)</param>
    /// <returns>Value at the specified percentile</returns>
    public double CalculatePercentile(IEnumerable<double> sortedValues, double percentile)
    {
        var sorted = sortedValues.ToList();
        if (!sorted.Any())
            return 0;

        var index = (percentile / 100) * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    /// <summary>
    /// Extract glucose values from entries, handling different data formats
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <returns>Collection of glucose values in mg/dL</returns>
    public IEnumerable<double> ExtractGlucoseValues(IEnumerable<Entry> entries)
    {
        return entries
            .Select(entry =>
                (entry.Sgv.HasValue && entry.Sgv.Value > 0) ? entry.Sgv.Value
                : (entry.Mgdl > 0) ? entry.Mgdl
                : 0
            )
            .Where(value => value > 0 && value < 600);
    }

    #endregion

    #region Glycemic Variability

    /// <summary>
    /// Calculate comprehensive glycemic variability metrics
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <param name="entries">Collection of glucose entries with timestamps</param>
    /// <returns>Comprehensive glycemic variability metrics</returns>
    public GlycemicVariability CalculateGlycemicVariability(
        IEnumerable<double> values,
        IEnumerable<Entry> entries
    )
    {
        var valuesList = values.ToList();
        var entriesList = entries.ToList();

        if (valuesList.Count < 2)
        {
            throw new ArgumentException(
                "Not enough data points to calculate glycemic variability metrics"
            );
        }

        var mean = valuesList.Average();
        var variance = valuesList.Sum(v => Math.Pow(v - mean, 2)) / valuesList.Count;
        var standardDeviation = Math.Sqrt(variance);
        var coefficientOfVariation = (standardDeviation / mean) * 100;

        var mage = CalculateMAGE(valuesList);
        var conga = CalculateCONGA(valuesList, 2);
        var adrr = CalculateADRR(valuesList);
        var labilityIndex = CalculateLabilityIndex(entriesList);
        var jIndex = CalculateJIndex(valuesList, mean);
        var hbgi = CalculateHBGI(valuesList);
        var lbgi = CalculateLBGI(valuesList);
        var gvi = CalculateGVI(valuesList, entriesList);
        var pgs = CalculatePGS(valuesList, gvi, mean);

        return new GlycemicVariability
        {
            CoefficientOfVariation = Math.Round(coefficientOfVariation * 10) / 10,
            StandardDeviation = Math.Round(standardDeviation * 10) / 10,
            MeanAmplitudeGlycemicExcursions = Math.Round(mage * 10) / 10,
            ContinuousOverlappingNetGlycemicAction = Math.Round(conga * 10) / 10,
            AverageDailyRiskRange = Math.Round(adrr * 10) / 10,
            LabilityIndex = Math.Round(labilityIndex * 10) / 10,
            JIndex = Math.Round(jIndex * 10) / 10,
            HighBloodGlucoseIndex = Math.Round(hbgi * 100) / 100,
            LowBloodGlucoseIndex = Math.Round(lbgi * 100) / 100,
            GlycemicVariabilityIndex = Math.Round(gvi * 100) / 100,
            PatientGlycemicStatus = Math.Round(pgs * 10) / 10,
            EstimatedA1c = CalculateEstimatedA1C(mean),
        };
    }

    /// <summary>
    /// Calculate estimated A1C from average glucose using the formula: A1C = (average glucose + 46.7) / 28.7
    /// </summary>
    /// <param name="averageGlucose">Average glucose in mg/dL</param>
    /// <returns>Estimated A1C percentage</returns>
    public double CalculateEstimatedA1C(double averageGlucose)
    {
        if (averageGlucose == 0)
            return 0;
        var a1c = (averageGlucose + 46.7) / 28.7;
        return a1c;
    }

    /// <summary>
    /// Calculate MAGE (Mean Amplitude of Glycemic Excursions)
    /// Average of all glycemic excursions (except excursion having value less than 1 SD from mean glucose) in a 24 h time period
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <returns>MAGE value</returns>
    public double CalculateMAGE(IEnumerable<double> values)
    {
        var valuesList = values.ToList();
        if (valuesList.Count < 3)
            return 0;

        var mean = valuesList.Average();
        var sd = Math.Sqrt(valuesList.Sum(v => Math.Pow(v - mean, 2)) / valuesList.Count);

        var excursions = new List<double>();
        string? currentDirection = null;
        var lastTurningPoint = valuesList[0];

        for (int i = 1; i < valuesList.Count; i++)
        {
            var diff = valuesList[i] - valuesList[i - 1];
            var newDirection =
                diff > 0 ? "up"
                : diff < 0 ? "down"
                : currentDirection;

            if (newDirection != currentDirection && currentDirection != null)
            {
                var excursion = Math.Abs(valuesList[i - 1] - lastTurningPoint);
                if (excursion > sd)
                {
                    excursions.Add(excursion);
                }
                lastTurningPoint = valuesList[i - 1];
            }

            currentDirection = newDirection;
        }

        return excursions.Any() ? excursions.Average() : 0;
    }

    /// <summary>
    /// Calculate CONGA (Continuous Overlapping Net Glycemic Action)
    /// Standard deviation of summated difference between current observation and previous observation
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <param name="hours">Number of hours for the window</param>
    /// <returns>CONGA value, or 0 if insufficient data</returns>
    public double CalculateCONGA(IEnumerable<double> values, int hours = 2)
    {
        var valuesList = values.ToList();
        const int interval = 5; // 5-minute intervals
        var pointsPerHour = 60 / interval;
        var windowSize = hours * pointsPerHour;

        if (valuesList.Count < windowSize)
        {
            // Return 0 instead of throwing exception when insufficient data
            return 0;
        }

        var differences = new List<double>();
        for (int i = 0; i <= valuesList.Count - windowSize; i++)
        {
            var diff = valuesList[i + windowSize - 1] - valuesList[i];
            differences.Add(Math.Pow(diff, 2));
        }

        var meanSquaredDiff = differences.Average();
        return Math.Sqrt(meanSquaredDiff);
    }

    /// <summary>
    /// Calculate ADRR (Average Daily Risk Range)
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <returns>ADRR value</returns>
    public double CalculateADRR(IEnumerable<double> values)
    {
        var logTransformed = values.Select(val => Math.Log(val)).ToList();
        var mean = logTransformed.Average();
        var variance = logTransformed.Sum(v => Math.Pow(v - mean, 2)) / logTransformed.Count();

        return Math.Sqrt(variance) * 100;
    }

    /// <summary>
    /// Calculate Lability Index
    /// </summary>
    /// <param name="entries">Collection of glucose entries with timestamps</param>
    /// <returns>Lability Index value</returns>
    public double CalculateLabilityIndex(IEnumerable<Entry> entries)
    {
        var entriesList = entries.ToList();
        if (entriesList.Count < 2)
            throw new ArgumentException(
                "Not enough data points to calculate Lability Index (requires at least 2).",
                nameof(entries)
            );

        double totalChange = 0;
        for (int i = 1; i < entriesList.Count; i++)
        {
            var prev =
                entriesList[i - 1].Sgv
                ?? (entriesList[i - 1].Mgdl > 0 ? entriesList[i - 1].Mgdl : 0);
            var curr = entriesList[i].Sgv ?? (entriesList[i].Mgdl > 0 ? entriesList[i].Mgdl : 0);
            totalChange += Math.Pow(curr - prev, 2);
        }

        return Math.Sqrt(totalChange / (entriesList.Count - 1));
    }

    /// <summary>
    /// Calculate J-Index
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <param name="mean">Mean glucose value</param>
    /// <returns>J-Index value</returns>
    public double CalculateJIndex(IEnumerable<double> values, double mean)
    {
        const double targetMean = 112; // Target glucose level in mg/dL
        var meanComponent = 0.324 * Math.Pow(mean - targetMean, 2);
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count();
        var variabilityComponent = 0.0018 * variance;

        return meanComponent + variabilityComponent;
    }

    /// <summary>
    /// Calculate HBGI (High Blood Glucose Index)
    /// Risk index for hyperglycemia based on Kovatchev et al. methodology
    /// Low (HBGI &lt;= 4.5), Moderate (4.5 &lt; HBGI &lt;= 9.0), and High (HBGI &gt; 9.0)
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <returns>HBGI value</returns>
    public double CalculateHBGI(IEnumerable<double> values)
    {
        var valuesList = values.ToList();
        if (!valuesList.Any())
            throw new ArgumentException(
                "Not enough data points to calculate HBGI.",
                nameof(values)
            );

        var riskSum = valuesList.Sum(glucose =>
        {
            // Kovatchev formula: f(BG) = 1.084 * (ln(BG/18)^1.084 - 1.928)
            var bgInMmol = glucose / 18;
            var logBG = Math.Log(bgInMmol);
            var fBG = 1.084 * (Math.Pow(logBG, 1.084) - 1.928);

            var risk = fBG > 0 ? 10 * Math.Pow(fBG, 2) : 0;
            return risk;
        });

        return riskSum / valuesList.Count;
    }

    /// <summary>
    /// Calculate LBGI (Low Blood Glucose Index)
    /// Risk index for hypoglycemia based on Kovatchev et al. methodology
    /// Minimal (LBGI &lt;= 1.1), Low (1.1 &lt; LBGI &lt;= 2.5), Moderate (2.5 &lt; LBGI &lt;= 5), and High (LBGI &gt; 5.0)
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <returns>LBGI value</returns>
    public double CalculateLBGI(IEnumerable<double> values)
    {
        var valuesList = values.ToList();
        if (!valuesList.Any())
            throw new ArgumentException(
                "Not enough data points to calculate LBGI.",
                nameof(values)
            );

        var riskSum = valuesList.Sum(glucose =>
        {
            // Kovatchev formula: f(BG) = 1.084 * (ln(BG/18)^1.084 - 1.928)
            var bgInMmol = glucose / 18;
            var logBG = Math.Log(bgInMmol);
            var fBG = 1.084 * (Math.Pow(logBG, 1.084) - 1.928);

            var risk = fBG < 0 ? 10 * Math.Pow(fBG, 2) : 0;
            return risk;
        });

        return riskSum / valuesList.Count;
    }

    /// <summary>
    /// Calculate GVI (Glycemic Variability Index)
    /// Measures the distance traveled by the glucose line if stretched out
    /// GVI = 1.0-1.2: low variability (non-diabetic), GVI = 1.2-1.5: modest variability, GVI > 1.5: high glycemic variability
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <param name="entries">Collection of glucose entries with timestamps</param>
    /// <returns>GVI value</returns>
    public double CalculateGVI(IEnumerable<double> values, IEnumerable<Entry> entries)
    {
        var valuesList = values.ToList();
        var entriesList = entries.ToList();

        if (valuesList.Count < 2 || entriesList.Count < 2)
            throw new ArgumentException(
                "Not enough data points to calculate GVI (requires at least 2).",
                nameof(values)
            );

        double actualDistance = 0;
        double idealTime = 0;

        for (int i = 0; i < entriesList.Count - 1; i++)
        {
            var currentEntry = entriesList[i];
            var nextEntry = entriesList[i + 1];

            var currentValue = currentEntry.Sgv ?? (currentEntry.Mgdl > 0 ? currentEntry.Mgdl : 0);
            var nextValue = nextEntry.Sgv ?? (nextEntry.Mgdl > 0 ? nextEntry.Mgdl : 0);

            if (currentValue <= 0 || nextValue <= 0)
                continue;

            var currentTime =
                currentEntry.Mills > 0
                    ? currentEntry.Mills
                    : (currentEntry.Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0);
            var nextTime =
                nextEntry.Mills > 0
                    ? nextEntry.Mills
                    : (nextEntry.Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0);

            var timeDelta = (nextTime - currentTime) / (1000.0 * 60); // Convert to minutes

            if (timeDelta > 15)
                continue; // Skip gaps > 15 minutes

            var glucoseDelta = Math.Abs(nextValue - currentValue);
            var distance = Math.Sqrt(Math.Pow(timeDelta, 2) + Math.Pow(glucoseDelta, 2));
            actualDistance += distance;
            idealTime += timeDelta;
        }

        if (idealTime == 0)
            return 1.0;

        var idealDistance = idealTime;
        return actualDistance / idealDistance;
    }

    /// <summary>
    /// Calculate PGS (Patient Glycemic Status)
    /// Combines GVI + mean glucose + percentage of time in range
    /// PGS ≤ 35: excellent glycemic status (non-diabetic), 35-100: good, 100-150: poor, >150: very poor
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <param name="gvi">Glycemic Variability Index</param>
    /// <param name="meanGlucose">Mean glucose value</param>
    /// <returns>PGS value, or 0 if no data</returns>
    public double CalculatePGS(IEnumerable<double> values, double gvi, double meanGlucose)
    {
        var valuesList = values.ToList();
        if (!valuesList.Any())
        {
            // Return 0 instead of throwing exception when no data
            return 0;
        }

        const double targetLow = 70;
        const double targetHigh = 180;

        var inRangeCount = valuesList.Count(val => val >= targetLow && val <= targetHigh);
        var percentTimeInRange = (double)inRangeCount / valuesList.Count;

        return gvi * meanGlucose * (1 - percentTimeInRange);
    }

    #endregion

    #region Time in Range

    /// <summary>
    /// Calculate time in range metrics
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <param name="thresholds">Glycemic thresholds (optional, uses defaults if not provided)</param>
    /// <returns>Time in range metrics including percentages, durations, and episodes</returns>
    public TimeInRangeMetrics CalculateTimeInRange(
        IEnumerable<Entry> entries,
        GlycemicThresholds? thresholds = null
    )
    {
        thresholds ??= new GlycemicThresholds();

        var entriesList = entries
            .Where(e => (e.Sgv ?? (e.Mgdl > 0 ? e.Mgdl : 0)) > 0)
            .OrderBy(e =>
                e.Mills > 0 ? e.Mills : (e.Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0)
            )
            .ToList();

        if (!entriesList.Any())
        {
            return new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages(),
                Durations = new TimeInRangeDurations(),
                Episodes = new TimeInRangeEpisodes(),
            };
        }

        var glucoseValues = ExtractGlucoseValues(entriesList).ToList();
        var totalReadings = glucoseValues.Count;

        if (totalReadings == 0)
        {
            return new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages(),
                Durations = new TimeInRangeDurations(),
                Episodes = new TimeInRangeEpisodes(),
            };
        }

        // Count readings in each range
        var severeLowCount = glucoseValues.Count(v => v < thresholds.SevereLow);
        var lowCount = glucoseValues.Count(v => v >= thresholds.SevereLow && v < thresholds.Low);
        var targetCount = glucoseValues.Count(v =>
            v >= thresholds.TargetBottom && v <= thresholds.TargetTop
        );
        var tightTargetCount = glucoseValues.Count(v =>
            v >= thresholds.TightTargetBottom && v <= thresholds.TightTargetTop
        );
        var highCount = glucoseValues.Count(v =>
            v > thresholds.TargetTop && v <= thresholds.SevereHigh
        );
        var severeHighCount = glucoseValues.Count(v => v > thresholds.SevereHigh);

        // Calculate percentages
        var percentages = new TimeInRangePercentages
        {
            SevereLow = (double)severeLowCount / totalReadings * 100,
            Low = (double)lowCount / totalReadings * 100,
            Target = (double)targetCount / totalReadings * 100,
            TightTarget = (double)tightTargetCount / totalReadings * 100,
            High = (double)highCount / totalReadings * 100,
            SevereHigh = (double)severeHighCount / totalReadings * 100,
        };

        // Calculate durations (assuming 5-minute intervals)
        const int intervalMinutes = 5;
        var durations = new TimeInRangeDurations
        {
            SevereLow = severeLowCount * intervalMinutes,
            Low = lowCount * intervalMinutes,
            Target = targetCount * intervalMinutes,
            TightTarget = tightTargetCount * intervalMinutes,
            High = highCount * intervalMinutes,
            SevereHigh = severeHighCount * intervalMinutes,
        };

        // Calculate episodes (simplified - consecutive readings in same range)
        var episodes = CalculateEpisodes(glucoseValues, thresholds);

        return new TimeInRangeMetrics
        {
            Percentages = percentages,
            Durations = durations,
            Episodes = episodes,
        };
    }

    private TimeInRangeEpisodes CalculateEpisodes(
        IList<double> glucoseValues,
        GlycemicThresholds thresholds
    )
    {
        var episodes = new TimeInRangeEpisodes();

        if (!glucoseValues.Any())
            return episodes;

        string? lastRange = null;
        var episodeCounts = new Dictionary<string, int>();

        foreach (var value in glucoseValues)
        {
            string currentRange;
            if (value < thresholds.SevereLow)
                currentRange = "SevereLow";
            else if (value < thresholds.Low)
                currentRange = "Low";
            else if (value > thresholds.SevereHigh)
                currentRange = "SevereHigh";
            else if (value > thresholds.TargetTop)
                currentRange = "High";
            else
                currentRange = "Target";

            if (
                currentRange != lastRange
                && (
                    currentRange == "SevereLow"
                    || currentRange == "Low"
                    || currentRange == "High"
                    || currentRange == "SevereHigh"
                )
            )
            {
                episodeCounts[currentRange] = episodeCounts.GetValueOrDefault(currentRange, 0) + 1;
            }

            lastRange = currentRange;
        }

        episodes.SevereLow = episodeCounts.GetValueOrDefault("SevereLow", 0);
        episodes.Low = episodeCounts.GetValueOrDefault("Low", 0);
        episodes.High = episodeCounts.GetValueOrDefault("High", 0);
        episodes.SevereHigh = episodeCounts.GetValueOrDefault("SevereHigh", 0);

        return episodes;
    }

    #endregion

    #region Glucose Distribution

    /// <summary>
    /// Calculate glucose distribution from entries using configurable bins
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <param name="bins">Distribution bins (optional, uses defaults if not provided)</param>
    /// <returns>Collection of distribution data points</returns>
    public IEnumerable<DistributionDataPoint> CalculateGlucoseDistribution(
        IEnumerable<Entry> entries,
        IEnumerable<DistributionBin>? bins = null
    )
    {
        var glucoseValues = ExtractGlucoseValues(entries);
        return CalculateGlucoseDistributionFromValues(glucoseValues, bins);
    }

    /// <summary>
    /// Calculate glucose distribution from raw glucose values
    /// </summary>
    /// <param name="glucoseValues">Collection of glucose values</param>
    /// <param name="bins">Distribution bins (optional, uses defaults if not provided)</param>
    /// <returns>Collection of distribution data points</returns>
    public IEnumerable<DistributionDataPoint> CalculateGlucoseDistributionFromValues(
        IEnumerable<double> glucoseValues,
        IEnumerable<DistributionBin>? bins = null
    )
    {
        bins ??= DefaultDistributionBins;

        var readings = glucoseValues.Where(value => value > 0 && value < 1000).ToList();

        if (!readings.Any())
        {
            return Enumerable.Empty<DistributionDataPoint>();
        }

        // Count readings in each bin
        var counts = bins.Select(bin => new DistributionDataPoint
            {
                Range = bin.Range,
                Count = readings.Count(reading => reading >= bin.Min && reading <= bin.Max),
                Percent = 0,
            })
            .ToList();

        // Calculate percentages
        var total = readings.Count;
        foreach (var bin in counts)
        {
            bin.Percent = total > 0 ? Math.Round((double)bin.Count / total * 100 * 10) / 10 : 0;
        }

        // Filter out empty bins
        return counts.Where(bin => bin.Count > 0);
    }

    /// <summary>
    /// Calculate estimated HbA1C as a formatted string
    /// </summary>
    /// <param name="values">Collection of glucose values</param>
    /// <returns>Estimated HbA1C as a formatted string</returns>
    public string CalculateEstimatedHbA1C(IEnumerable<double> values)
    {
        var mean = CalculateMean(values);
        if (mean == 0)
            return "0.0";
        var a1c = (mean + 46.7) / 28.7;
        return a1c.ToString("F1");
    }

    /// <summary>
    /// Calculate averaged statistics for each hour of the day (0-23)
    /// Groups glucose readings by hour across multiple days and calculates BasicGlucoseStats for each hour
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <returns>Collection of averaged statistics for each hour</returns>
    public IEnumerable<AveragedStats> CalculateAveragedStats(IEnumerable<Entry> entries)
    {
        var entriesList = entries.ToList();

        // Group entries by hour of day
        var hourlyGroups = new Dictionary<int, List<Entry>>();

        // Initialize all 24 hours
        for (int hour = 0; hour < 24; hour++)
        {
            hourlyGroups[hour] = new List<Entry>();
        }

        // Group entries by hour, handling different timestamp formats (only if we have entries)
        if (entriesList.Any())
        {
            foreach (var entry in entriesList)
            {
                DateTime date;

                if (entry.Mills > 0)
                {
                    date = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).DateTime;
                }
                else if (entry.Date.HasValue)
                {
                    date = entry.Date.Value;
                }
                else
                {
                    continue; // Skip entries without valid timestamps
                }

                var hour = date.Hour;
                if (hour >= 0 && hour < 24)
                {
                    hourlyGroups[hour].Add(entry);
                }
            }
        }

        // Calculate statistics for each hour
        var averagedStats = new List<AveragedStats>();

        for (int hourIndex = 0; hourIndex < 24; hourIndex++)
        {
            var hourEntries = hourlyGroups[hourIndex];

            // Extract glucose values and calculate basic stats
            var glucoseValues = ExtractGlucoseValues(hourEntries);
            var basicStats = CalculateBasicStats(glucoseValues);

            var hourlyStats = new AveragedStats
            {
                Hour = hourIndex,
                Count = basicStats.Count,
                Mean = basicStats.Mean,
                Median = basicStats.Median,
                Min = basicStats.Min,
                Max = basicStats.Max,
                StandardDeviation = basicStats.StandardDeviation,
                Percentiles = basicStats.Percentiles,
            };

            averagedStats.Add(hourlyStats);
        }

        return averagedStats;
    }

    #endregion

    #region Treatment Statistics

    /// <summary>
    /// Calculate treatment summary for a collection of treatments
    /// </summary>
    /// <param name="treatments">Collection of treatments</param>
    /// <returns>Treatment summary with totals and counts</returns>
    public TreatmentSummary CalculateTreatmentSummary(IEnumerable<Treatment> treatments)
    {
        var summary = new TreatmentSummary
        {
            Totals = new TreatmentTotals { Food = new FoodTotals(), Insulin = new InsulinTotals() },
            TreatmentCount = 0,
        };

        foreach (var treatment in treatments)
        {
            // Count treatments with actual data
            if (
                treatment.Insulin.HasValue
                || treatment.Carbs.HasValue
                || treatment.Protein.HasValue
                || treatment.Fat.HasValue
                || (treatment.Rate.HasValue && treatment.Duration.HasValue)
            )
            {
                summary.TreatmentCount++;
            }

            // Aggregate insulin
            if (treatment.Insulin.HasValue)
            {
                if (IsBolusTreatment(treatment))
                {
                    summary.Totals.Insulin.Bolus += treatment.Insulin.Value;
                }
                else
                {
                    summary.Totals.Insulin.Basal += treatment.Insulin.Value;
                }
            }
            // Fallback: Calculate basal insulin from rate × duration for legacy data
            // that has rate/duration but no pre-calculated insulin value
            else if (
                treatment.Rate.HasValue
                && treatment.Duration.HasValue
                && !IsBolusTreatment(treatment)
            )
            {
                // Rate is U/hr, Duration is in minutes
                var basalInsulin = (treatment.Rate.Value * treatment.Duration.Value) / 60.0;
                if (basalInsulin > 0)
                {
                    summary.Totals.Insulin.Basal += basalInsulin;
                }
            }

            // Aggregate macronutrients
            if (treatment.Carbs.HasValue)
                summary.Totals.Food.Carbs += treatment.Carbs.Value;
            if (treatment.Protein.HasValue)
                summary.Totals.Food.Protein += treatment.Protein.Value;
            if (treatment.Fat.HasValue)
                summary.Totals.Food.Fat += treatment.Fat.Value;
        }

        return summary;
    }

    /// <summary>
    /// Calculate overall averages across multiple days
    /// </summary>
    /// <param name="dailyDataPoints">Collection of daily data points</param>
    /// <returns>Overall averages or null if no data</returns>
    public OverallAverages? CalculateOverallAverages(IEnumerable<DayData> dailyDataPoints)
    {
        var dataPoints = dailyDataPoints.ToList();
        if (!dataPoints.Any())
            return null;

        var totals = dataPoints.Aggregate(
            new
            {
                TotalDailyInsulin = 0.0,
                BolusInsulin = 0.0,
                BasalInsulin = 0.0,
                TotalCarbs = 0.0,
                TotalProtein = 0.0,
                TotalFat = 0.0,
                TimeInRange = 0.0,
                TightTimeInRange = 0.0,
                DaysWithData = 0,
            },
            (acc, day) =>
            {
                var totalDailyInsulin = GetTotalInsulin(day.TreatmentSummary);
                var bolusInsulin = day.TreatmentSummary.Totals.Insulin.Bolus;
                var basalInsulin = day.TreatmentSummary.Totals.Insulin.Basal;

                return new
                {
                    TotalDailyInsulin = acc.TotalDailyInsulin + totalDailyInsulin,
                    BolusInsulin = acc.BolusInsulin + bolusInsulin,
                    BasalInsulin = acc.BasalInsulin + basalInsulin,
                    TotalCarbs = acc.TotalCarbs + day.TreatmentSummary.Totals.Food.Carbs,
                    TotalProtein = acc.TotalProtein + day.TreatmentSummary.Totals.Food.Protein,
                    TotalFat = acc.TotalFat + day.TreatmentSummary.Totals.Food.Fat,
                    TimeInRange = acc.TimeInRange + day.TimeInRanges.Percentages.Target,
                    TightTimeInRange = acc.TightTimeInRange
                        + (
                            day.TimeInRanges.Percentages.Target > 85
                                ? day.TimeInRanges.Percentages.Target
                                : 0
                        ),
                    DaysWithData = acc.DaysWithData + (totalDailyInsulin > 0 ? 1 : 0),
                };
            }
        );

        var daysCount = Math.Max(totals.DaysWithData, 1);
        var avgTotalDaily = totals.TotalDailyInsulin / daysCount;
        var avgBolus = totals.BolusInsulin / daysCount;
        var avgBasal = totals.BasalInsulin / daysCount;

        return new OverallAverages
        {
            AvgTotalDaily = avgTotalDaily,
            AvgBolus = avgBolus,
            AvgBasal = avgBasal,
            BolusPercentage = avgTotalDaily > 0 ? (avgBolus / avgTotalDaily) * 100 : 0,
            BasalPercentage = avgTotalDaily > 0 ? (avgBasal / avgTotalDaily) * 100 : 0,
            AvgCarbs = totals.TotalCarbs / daysCount,
            AvgProtein = totals.TotalProtein / daysCount,
            AvgFat = totals.TotalFat / daysCount,
            AvgTimeInRange = totals.TimeInRange / dataPoints.Count,
            AvgTightTimeInRange = totals.TightTimeInRange / dataPoints.Count,
        };
    }

    /// <summary>
    /// Calculate total insulin from treatment summary
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary</param>
    /// <returns>Total insulin (bolus + basal)</returns>
    public double GetTotalInsulin(TreatmentSummary treatmentSummary)
    {
        return treatmentSummary.Totals.Insulin.Bolus + treatmentSummary.Totals.Insulin.Basal;
    }

    /// <summary>
    /// Calculate bolus percentage of total insulin
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary</param>
    /// <returns>Bolus percentage</returns>
    public double GetBolusPercentage(TreatmentSummary treatmentSummary)
    {
        var total = GetTotalInsulin(treatmentSummary);
        return total > 0 ? (treatmentSummary.Totals.Insulin.Bolus / total) * 100 : 0;
    }

    /// <summary>
    /// Calculate basal percentage of total insulin
    /// </summary>
    /// <param name="treatmentSummary">Treatment summary</param>
    /// <returns>Basal percentage</returns>
    public double GetBasalPercentage(TreatmentSummary treatmentSummary)
    {
        var total = GetTotalInsulin(treatmentSummary);
        return total > 0 ? (treatmentSummary.Totals.Insulin.Basal / total) * 100 : 0;
    }

    /// <summary>
    /// Determine if a treatment is a bolus based on event type
    /// Uses the actual event types discovered from the Nightscout server
    /// </summary>
    /// <param name="treatment">Treatment to check</param>
    /// <returns>True if the treatment is a bolus type</returns>
    public bool IsBolusTreatment(Treatment treatment)
    {
        return BolusTreatmentTypes.Contains(treatment.EventType, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Formatting Utilities

    /// <summary>
    /// Format insulin values for display with appropriate precision
    /// Uses "shifted" display format where values like 0.05 display as ".05"
    /// </summary>
    /// <param name="value">Insulin value</param>
    /// <returns>Formatted insulin string</returns>
    public string FormatInsulinDisplay(double value)
    {
        if (value == 0)
        {
            return "0";
        }

        var formattedValue = value.ToString("F2");

        // Apply shift formatting - remove leading zero for values less than 1
        if (value < 1 && value > 0)
        {
            formattedValue = Regex.Replace(formattedValue, "^0", "");
        }

        return formattedValue;
    }

    /// <summary>
    /// Format carb values for display with appropriate precision
    /// Uses "shifted" display format where values like 0.5 display as ".5"
    /// </summary>
    /// <param name="value">Carb value</param>
    /// <returns>Formatted carb string</returns>
    public string FormatCarbDisplay(double value)
    {
        if (value == 0)
        {
            return "0";
        }

        var formattedValue = value.ToString("F1");

        // Apply shift formatting - remove leading zero for values less than 1
        if (value < 1 && value > 0)
        {
            formattedValue = Regex.Replace(formattedValue, "^0", "");
        }

        return formattedValue;
    }

    /// <summary>
    /// Format percentage values for display
    /// </summary>
    /// <param name="value">Percentage value</param>
    /// <returns>Formatted percentage string</returns>
    public string FormatPercentageDisplay(double value)
    {
        return value.ToString("F1");
    }

    /// <summary>
    /// Round insulin values to pump precision (typically 0.05 units)
    /// </summary>
    /// <param name="value">Insulin value</param>
    /// <param name="step">Precision step (default 0.05)</param>
    /// <returns>Rounded insulin value</returns>
    public double RoundInsulinToPumpPrecision(double value, double step = 0.05)
    {
        return Math.Round(value / step) * step;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Validate treatment data for completeness and consistency
    /// </summary>
    /// <param name="treatment">Treatment to validate</param>
    /// <returns>True if treatment data is valid</returns>
    public bool ValidateTreatmentData(Treatment treatment)
    {
        // Basic validation
        if (treatment.Timestamp == null || string.IsNullOrEmpty(treatment.Id))
            return false;

        // Check for at least one meaningful value
        if (
            !treatment.Insulin.HasValue
            && !treatment.Carbs.HasValue
            && !treatment.Protein.HasValue
            && !treatment.Fat.HasValue
        )
        {
            return false;
        }

        // Validate numeric values
        if (
            treatment.Insulin.HasValue
            && (double.IsNaN(treatment.Insulin.Value) || treatment.Insulin.Value < 0)
        )
            return false;
        if (
            treatment.Carbs.HasValue
            && (double.IsNaN(treatment.Carbs.Value) || treatment.Carbs.Value < 0)
        )
            return false;
        if (
            treatment.Protein.HasValue
            && (double.IsNaN(treatment.Protein.Value) || treatment.Protein.Value < 0)
        )
            return false;
        if (
            treatment.Fat.HasValue && (double.IsNaN(treatment.Fat.Value) || treatment.Fat.Value < 0)
        )
            return false;

        return true;
    }

    /// <summary>
    /// Filter and clean treatment data
    /// </summary>
    /// <param name="treatments">Collection of treatments to clean</param>
    /// <returns>Cleaned collection of treatments</returns>
    public IEnumerable<Treatment> CleanTreatmentData(IEnumerable<Treatment> treatments)
    {
        return treatments.Where(ValidateTreatmentData);
    }

    #endregion

    #region Unit Conversions

    /// <summary>
    /// Convert mg/dL to mmol/L
    /// </summary>
    /// <param name="mgdl">Glucose value in mg/dL</param>
    /// <returns>Glucose value in mmol/L</returns>
    public double MgdlToMMOL(double mgdl)
    {
        return Math.Round((mgdl / 18.01559) * 10) / 10;
    }

    /// <summary>
    /// Convert mmol/L to mg/dL
    /// </summary>
    /// <param name="mmol">Glucose value in mmol/L</param>
    /// <returns>Glucose value in mg/dL</returns>
    public double MmolToMGDL(double mmol)
    {
        return Math.Round(mmol * 18.01559);
    }

    /// <summary>
    /// Convert mg/dL to mmol/L as a formatted string
    /// </summary>
    /// <param name="mgdl">Glucose value in mg/dL</param>
    /// <returns>Glucose value in mmol/L as a formatted string</returns>
    public string MgdlToMMOLString(double mgdl)
    {
        return MgdlToMMOL(mgdl).ToString("F1");
    }

    #endregion

    #region Comprehensive Analytics

    /// <summary>
    /// Master glucose analytics function that calculates comprehensive glucose metrics
    /// with sensor-specific optimizations
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <param name="treatments">Collection of treatments (optional)</param>
    /// <param name="config">Extended analysis configuration (optional)</param>
    /// <returns>Comprehensive glucose analytics</returns>
    public GlucoseAnalytics AnalyzeGlucoseData(
        IEnumerable<Entry> entries,
        IEnumerable<Treatment> treatments,
        ExtendedAnalysisConfig? config = null
    )
    {
        config ??= new ExtendedAnalysisConfig();
        var glucoseValues = ExtractGlucoseValues(entries).ToList();

        if (!glucoseValues.Any())
        {
            return new GlucoseAnalytics
            {
                Time = new AnalysisTime
                {
                    Start = 0,
                    End = 0,
                    TimeOfAnalysis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                BasicStats = new BasicGlucoseStats(),
                TimeInRange = new TimeInRangeMetrics(),
                GlycemicVariability = new GlycemicVariability(),
                DataQuality = new DataQuality(),
            };
        }

        var sortedEntries = entries
            .Where(entry => (entry.Sgv ?? (entry.Mgdl > 0 ? entry.Mgdl : 0)) > 0)
            .OrderBy(entry =>
                entry.Mills > 0
                    ? entry.Mills
                    : (entry.Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0)
            )
            .ToList();

        var basicStats = CalculateBasicStats(glucoseValues);
        var timeInRange = CalculateTimeInRange(sortedEntries, config.Thresholds);
        var glycemicVariability = CalculateGlycemicVariability(glucoseValues, sortedEntries);
        var dataQuality = AssessDataQuality(sortedEntries);

        var timeStart =
            sortedEntries.FirstOrDefault()?.Mills
            ?? sortedEntries.FirstOrDefault()?.Date?.Ticks / TimeSpan.TicksPerMillisecond
            ?? 0;
        var timeEnd =
            sortedEntries.LastOrDefault()?.Mills
            ?? sortedEntries.LastOrDefault()?.Date?.Ticks / TimeSpan.TicksPerMillisecond
            ?? 0;

        return new GlucoseAnalytics
        {
            Time = new AnalysisTime
            {
                Start = timeStart,
                End = timeEnd,
                TimeOfAnalysis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            BasicStats = basicStats,
            TimeInRange = timeInRange,
            GlycemicVariability = glycemicVariability,
            DataQuality = dataQuality,
        };
    }

    private DataQuality AssessDataQuality(IList<Entry> entries)
    {
        var totalReadings = entries.Count;
        var gaps = new List<DataGap>();

        if (entries.Count > 1)
        {
            for (int i = 1; i < entries.Count; i++)
            {
                var prevTime =
                    entries[i - 1].Mills > 0
                        ? entries[i - 1].Mills
                        : (entries[i - 1].Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0);
                var currentTime =
                    entries[i].Mills > 0
                        ? entries[i].Mills
                        : (entries[i].Date?.Ticks / TimeSpan.TicksPerMillisecond ?? 0);
                var gapMinutes = (currentTime - prevTime) / (1000.0 * 60);

                if (gapMinutes > 15) // Gap larger than expected 5-10 minute interval
                {
                    gaps.Add(
                        new DataGap
                        {
                            Start = prevTime,
                            End = currentTime,
                            Duration = gapMinutes,
                        }
                    );
                }
            }
        }

        var longestGap = gaps.Any() ? gaps.Max(g => g.Duration) : 0;
        var averageGap = gaps.Any() ? gaps.Average(g => g.Duration) : 0;

        return new DataQuality
        {
            TotalReadings = totalReadings,
            MissingReadings = gaps.Sum(g => (int)(g.Duration / 5)), // Assuming 5-minute intervals
            DataCompleteness =
                totalReadings > 0 ? (1.0 - (double)gaps.Count / totalReadings) * 100 : 0,
            CgmActivePercent = 100, // Simplified - would need more sophisticated calculation
            GapAnalysis = new GapAnalysis
            {
                Gaps = gaps,
                LongestGap = longestGap,
                AverageGap = averageGap,
            },
            NoiseLevel = 0, // Would require noise analysis
            CalibrationEvents = 0, // Would require analysis of cal entries
            SensorWarmups = 0, // Would require analysis of sensor events
        };
    }

    #endregion
}
