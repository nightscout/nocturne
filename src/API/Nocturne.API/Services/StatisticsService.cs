using System.Text.RegularExpressions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

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
        "Bolus", // Generic bolus
        "SMB", // Super Micro Bolus (from loop systems like AndroidAPS)
        "e-Bolus", // Extended bolus
        "Extended Bolus", // Extended bolus variant
        "Dual Wave", // Combo bolus with extended portion
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
                Interpretation = GlucoseManagementIndicatorLevel.NonDiabetic,
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

        var hypoComponent = (veryLowWeight * percentages.VeryLow) + (lowWeight * percentages.Low);
        var hyperComponent =
            (veryHighWeight * percentages.VeryHigh) + (highWeight * percentages.High);

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
            GRIZone.A => GlycomicRiskInterpretation.Excellent,
            GRIZone.B => GlycomicRiskInterpretation.Good,
            GRIZone.C => GlycomicRiskInterpretation.Moderate,
            GRIZone.D => GlycomicRiskInterpretation.Suboptimal,
            GRIZone.E => GlycomicRiskInterpretation.Poor,
            _ => GlycomicRiskInterpretation.Unknown,
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
        var totalTBR = tir.VeryLow + tir.Low;
        assessment.TBRAssessment = AssessMaximumTarget(
            "Time Below Range",
            totalTBR,
            targets.MaxTBR
        );

        // Very Low Assessment (maximum target)
        assessment.VeryLowAssessment = AssessMaximumTarget(
            "Time Very Low (<54)",
            tir.VeryLow,
            targets.MaxTBRVeryLow
        );

        // Time Above Range Assessment (maximum target)
        var totalTAR = tir.VeryHigh + tir.High;
        assessment.TARAssessment = AssessMaximumTarget(
            "Time Above Range",
            totalTAR,
            targets.MaxTAR
        );

        // Very High Assessment (maximum target)
        assessment.VeryHighAssessment = AssessMaximumTarget(
            "Time Very High (>250)",
            tir.VeryHigh,
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
            6 => ClinicalAssessmentLevel.Excellent,
            >= 4 => ClinicalAssessmentLevel.Good,
            >= 2 => ClinicalAssessmentLevel.NeedsAttention,
            _ => ClinicalAssessmentLevel.NeedsSignificantImprovement,
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
            assessment.Strengths.Add(new LocalizedInsight
            {
                Key = InsightKey.TimeInRangeExcellent,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round(tir.Target * 10) / 10 },
                    { "target", targets.TargetTIR },
                }
            });

        if (assessment.VeryLowAssessment.Status == TargetStatus.Met && tir.VeryLow == 0)
            assessment.Strengths.Add(new LocalizedInsight
            {
                Key = InsightKey.NoSevereHypoglycemia,
                Context = new(),
            });

        if (assessment.CVAssessment.Status == TargetStatus.Met)
            assessment.Strengths.Add(new LocalizedInsight
            {
                Key = InsightKey.VariabilityControlled,
                Context = new Dictionary<string, double> { { "cv", Math.Round(cv * 10) / 10 } }
            });

        // Priority areas for improvement
        if (assessment.VeryLowAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add(new LocalizedInsight
            {
                Key = InsightKey.ReduceSevereHypoglycemia,
                Context = new(),
            });
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.TimeVeryLow,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round(tir.VeryLow * 10) / 10 },
                    { "target", targets.MaxTBRVeryLow },
                }
            });
        }

        if (assessment.TBRAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add(new LocalizedInsight
            {
                Key = InsightKey.ReduceHypoglycemia,
                Context = new(),
            });
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.TimeBelowRange,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round((tir.VeryLow + tir.Low) * 10) / 10 },
                    { "target", targets.MaxTBR },
                }
            });
        }

        if (assessment.TIRAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add(new LocalizedInsight
            {
                Key = InsightKey.IncreaseTIR,
                Context = new(),
            });
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.TimeInRange,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round(tir.Target * 10) / 10 },
                    { "target", targets.TargetTIR },
                }
            });
        }

        if (assessment.VeryHighAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add(new LocalizedInsight
            {
                Key = InsightKey.ReduceSevereHyperglycemia,
                Context = new(),
            });
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.TimeVeryHigh,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round(tir.VeryHigh * 10) / 10 },
                    { "target", targets.MaxTARVeryHigh },
                }
            });
        }

        if (assessment.CVAssessment.Status == TargetStatus.NotMet)
        {
            assessment.PriorityAreas.Add(new LocalizedInsight
            {
                Key = InsightKey.ReduceVariability,
                Context = new(),
            });
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.Variability,
                Context = new Dictionary<string, double>
                {
                    { "actual", Math.Round(cv * 10) / 10 },
                    { "target", targets.TargetCV },
                }
            });
        }

        // If everything is good
        if (assessment.TargetsMet == assessment.TotalTargets)
        {
            assessment.ActionableInsights.Add(new LocalizedInsight
            {
                Key = InsightKey.AllTargetsAchieved,
                Context = new(),
            });
        }
    }

    /// <summary>
    /// Check if there is sufficient data for a valid clinical report
    /// Per international guidelines, minimum 70% data coverage is required
    /// </summary>
    public DataSufficiencyAssessment AssessDataSufficiency(
        IEnumerable<SensorGlucose> entries,
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
            .Where(e => e.Mills > 0)
            .GroupBy(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Mills).Date)
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
    /// Assess the reliability of a statistics block based on data duration and reading count.
    /// Returns raw facts so the frontend can compose a plain-English reliability message.
    /// Clinical standard: 14 days of data with ≥70% completeness (per Bergenstal et al. / TIR Consensus).
    /// </summary>
    public StatisticReliability AssessReliability(
        int daysOfData,
        int readingCount,
        int recommendedMinimumDays = 14
    )
    {
        var meetsReliability = daysOfData >= recommendedMinimumDays && readingCount >= 1;

        return new StatisticReliability
        {
            MeetsReliabilityCriteria = meetsReliability,
            DaysOfData = daysOfData,
            RecommendedMinimumDays = recommendedMinimumDays,
            ReadingCount = readingCount,
        };
    }

    /// <summary>
    /// Calculate extended glucose analytics including GMI, GRI, and clinical assessment
    /// </summary>
    public ExtendedGlucoseAnalytics AnalyzeGlucoseDataExtended(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        ExtendedAnalysisConfig? config = null
    )
    {
        var entriesList = entries.ToList();
        var bolusesList = boluses.ToList();
        var carbIntakesList = carbIntakes.ToList();

        // Get base analytics
        var baseAnalytics = AnalyzeGlucoseData(entriesList, bolusesList, carbIntakesList, config);

        // Calculate GMI
        var gmi = CalculateGMI(baseAnalytics.BasicStats.Mean);

        // Calculate GRI
        var gri = CalculateGRI(baseAnalytics.TimeInRange);

        // Assess against clinical targets
        var clinicalAssessment = AssessAgainstTargets(baseAnalytics, population);

        // Assess data sufficiency (default 14 days)
        var dataSufficiency = AssessDataSufficiency(entriesList, 14);

        // Calculate treatment summary if data available
        TreatmentSummary? treatmentSummary = null;
        if (bolusesList.Any() || carbIntakesList.Any())
        {
            treatmentSummary = CalculateTreatmentSummary(bolusesList, carbIntakesList);
        }

        // Propagate reliability from base analytics to GMI
        gmi.Reliability = baseAnalytics.Reliability;

        return new ExtendedGlucoseAnalytics
        {
            // Base analytics properties
            Time = baseAnalytics.Time,
            BasicStats = baseAnalytics.BasicStats,
            TimeInRange = baseAnalytics.TimeInRange,
            GlycemicVariability = baseAnalytics.GlycemicVariability,
            DataQuality = baseAnalytics.DataQuality,
            Reliability = baseAnalytics.Reliability,

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
    /// Extract glucose values from entries
    /// </summary>
    /// <param name="entries">Collection of glucose entries</param>
    /// <returns>Collection of glucose values in mg/dL</returns>
    public IEnumerable<double> ExtractGlucoseValues(IEnumerable<SensorGlucose> entries)
    {
        return entries.Select(entry => entry.Mgdl).Where(value => value > 0 && value < 600);
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
        IEnumerable<SensorGlucose> entries
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
        var variance =
            valuesList.Count > 1
                ? valuesList.Sum(v => Math.Pow(v - mean, 2)) / (valuesList.Count - 1)
                : 0;
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

        // Calculate Mean Total Daily Change and Time in Fluctuation
        var (meanTotalDailyChange, timeInFluctuation) = CalculateFluctuationMetrics(entriesList);

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
            Gmi = CalculateGMI(mean),
            MeanTotalDailyChange = Math.Round(meanTotalDailyChange),
            TimeInFluctuation = Math.Round(timeInFluctuation * 10) / 10,
        };
    }

    /// <summary>
    /// Calculate Mean Total Daily Change and Time in Fluctuation metrics
    /// </summary>
    private (double MeanTotalDailyChange, double TimeInFluctuation) CalculateFluctuationMetrics(
        IReadOnlyList<SensorGlucose> entries
    )
    {
        if (entries.Count < 2)
        {
            return (0, 0);
        }

        // Sort entries by time
        var sortedEntries = entries
            .Where(e => e.Mgdl > 0 && e.Mills > 0)
            .OrderBy(e => e.Mills)
            .ToList();

        if (sortedEntries.Count < 2)
        {
            return (0, 0);
        }

        double totalChange = 0;
        int fluctuationCount = 0;
        int totalReadings = sortedEntries.Count;

        for (int i = 1; i < sortedEntries.Count; i++)
        {
            var prev = sortedEntries[i - 1];
            var curr = sortedEntries[i];

            var prevGlucose = prev.Mgdl;
            var currGlucose = curr.Mgdl;
            var glucoseDiff = Math.Abs(currGlucose - prevGlucose);

            totalChange += glucoseDiff;

            // Check for fluctuation (>15 mg/dL within 5-6 minutes)
            var timeDiff = curr.Mills - prev.Mills;
            if (timeDiff <= 6 * 60 * 1000 && glucoseDiff > 15)
            {
                fluctuationCount++;
            }
        }

        // Calculate number of days in dataset
        var firstTime = sortedEntries.First().Mills;
        var lastTime = sortedEntries.Last().Mills;
        var numDays = Math.Max(1, (lastTime - firstTime) / (24.0 * 60 * 60 * 1000));

        var meanTotalDailyChange = totalChange / numDays;
        var timeInFluctuation = (fluctuationCount / (double)totalReadings) * 100;

        return (meanTotalDailyChange, timeInFluctuation);
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
    public double CalculateLabilityIndex(IEnumerable<SensorGlucose> entries)
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
            var prev = entriesList[i - 1].Mgdl;
            var curr = entriesList[i].Mgdl;
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
    public double CalculateGVI(IEnumerable<double> values, IEnumerable<SensorGlucose> entries)
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

            var currentValue = currentEntry.Mgdl;
            var nextValue = nextEntry.Mgdl;

            if (currentValue <= 0 || nextValue <= 0)
                continue;

            var currentTime = currentEntry.Mills;
            var nextTime = nextEntry.Mills;

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
        IEnumerable<SensorGlucose> entries,
        GlycemicThresholds? thresholds = null
    )
    {
        thresholds ??= new GlycemicThresholds();

        var entriesList = entries.Where(e => e.Mgdl > 0).OrderBy(e => e.Mills).ToList();

        if (!entriesList.Any())
        {
            return new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages(),
                Durations = new TimeInRangeDurations(),
                Episodes = new TimeInRangeEpisodes(),
                RangeStats = new TimeInRangeDetailedStats(),
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
                RangeStats = new TimeInRangeDetailedStats(),
            };
        }

        // Count readings in each range
        var veryLowCount = glucoseValues.Count(v => v < thresholds.VeryLow);
        var lowCount = glucoseValues.Count(v => v >= thresholds.VeryLow && v < thresholds.Low);
        var targetCount = glucoseValues.Count(v =>
            v >= thresholds.TargetBottom && v <= thresholds.TargetTop
        );
        var tightTargetCount = glucoseValues.Count(v =>
            v >= thresholds.TightTargetBottom && v <= thresholds.TightTargetTop
        );
        var highCount = glucoseValues.Count(v =>
            v > thresholds.TargetTop && v <= thresholds.VeryHigh
        );
        var veryHighCount = glucoseValues.Count(v => v > thresholds.VeryHigh);

        // Calculate percentages
        var percentages = new TimeInRangePercentages
        {
            VeryLow = (double)veryLowCount / totalReadings * 100,
            Low = (double)lowCount / totalReadings * 100,
            Target = (double)targetCount / totalReadings * 100,
            TightTarget = (double)tightTargetCount / totalReadings * 100,
            High = (double)highCount / totalReadings * 100,
            VeryHigh = (double)veryHighCount / totalReadings * 100,
        };

        // Calculate durations (assuming 5-minute intervals)
        const int intervalMinutes = 5;
        var durations = new TimeInRangeDurations
        {
            VeryLow = veryLowCount * intervalMinutes,
            Low = lowCount * intervalMinutes,
            Target = targetCount * intervalMinutes,
            TightTarget = tightTargetCount * intervalMinutes,
            High = highCount * intervalMinutes,
            VeryHigh = veryHighCount * intervalMinutes,
        };

        // Calculate episodes (simplified - consecutive readings in same range)
        var episodes = CalculateEpisodes(glucoseValues, thresholds);

        // Calculate per-range detailed statistics
        var lowValues = glucoseValues.Where(v => v < thresholds.Low).ToList();
        var targetValues = glucoseValues
            .Where(v => v >= thresholds.TargetBottom && v <= thresholds.TargetTop)
            .ToList();
        var highValues = glucoseValues.Where(v => v > thresholds.TargetTop).ToList();

        var rangeStats = new TimeInRangeDetailedStats
        {
            Low = CalculateRangeMetrics("Low", lowValues, totalReadings),
            Target = CalculateRangeMetrics("In Range", targetValues, totalReadings),
            High = CalculateRangeMetrics("High", highValues, totalReadings),
        };

        return new TimeInRangeMetrics
        {
            Percentages = percentages,
            Durations = durations,
            Episodes = episodes,
            RangeStats = rangeStats,
        };
    }

    /// <summary>
    /// Calculate PeriodMetrics for a specific glucose range
    /// </summary>
    private PeriodMetrics CalculateRangeMetrics(
        string rangeName,
        List<double> values,
        int totalReadings
    )
    {
        if (values.Count == 0)
        {
            return new PeriodMetrics { PeriodName = rangeName };
        }

        var mean = values.Average();
        var sortedValues = values.OrderBy(v => v).ToList();
        var median =
            sortedValues.Count % 2 == 0
                ? (sortedValues[sortedValues.Count / 2 - 1] + sortedValues[sortedValues.Count / 2])
                    / 2
                : sortedValues[sortedValues.Count / 2];
        var variance =
            values.Count > 1 ? values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1) : 0;
        var stdDev = Math.Sqrt(variance);

        return new PeriodMetrics
        {
            PeriodName = rangeName,
            ReadingCount = values.Count,
            Mean = Math.Round(mean, 1),
            Median = Math.Round(median, 1),
            StandardDeviation = Math.Round(stdDev, 1),
            TimeInRange = Math.Round((double)values.Count / totalReadings * 100, 1),
            Min = values.Min(),
            Max = values.Max(),
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
            if (value < thresholds.VeryLow)
                currentRange = "VeryLow";
            else if (value < thresholds.Low)
                currentRange = "Low";
            else if (value > thresholds.VeryHigh)
                currentRange = "VeryHigh";
            else if (value > thresholds.TargetTop)
                currentRange = "High";
            else
                currentRange = "Target";

            if (
                currentRange != lastRange
                && (
                    currentRange == "VeryLow"
                    || currentRange == "Low"
                    || currentRange == "High"
                    || currentRange == "VeryHigh"
                )
            )
            {
                episodeCounts[currentRange] = episodeCounts.GetValueOrDefault(currentRange, 0) + 1;
            }

            lastRange = currentRange;
        }

        episodes.VeryLow = episodeCounts.GetValueOrDefault("VeryLow", 0);
        episodes.Low = episodeCounts.GetValueOrDefault("Low", 0);
        episodes.High = episodeCounts.GetValueOrDefault("High", 0);
        episodes.VeryHigh = episodeCounts.GetValueOrDefault("VeryHigh", 0);

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
        IEnumerable<SensorGlucose> entries,
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
    public IEnumerable<AveragedStats> CalculateAveragedStats(IEnumerable<SensorGlucose> entries)
    {
        var entriesList = entries.ToList();

        // Group entries by hour of day
        var hourlyGroups = new Dictionary<int, List<SensorGlucose>>();

        // Initialize all 24 hours
        for (int hour = 0; hour < 24; hour++)
        {
            hourlyGroups[hour] = new List<SensorGlucose>();
        }

        // Group entries by hour (only if we have entries)
        if (entriesList.Any())
        {
            foreach (var entry in entriesList)
            {
                if (entry.Mills <= 0)
                {
                    continue; // Skip entries without valid timestamps
                }

                var dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills);
                if (entry.UtcOffset.HasValue)
                {
                    dateTimeOffset = dateTimeOffset.ToOffset(
                        TimeSpan.FromMinutes(entry.UtcOffset.Value)
                    );
                }

                var hour = dateTimeOffset.Hour;
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
            var glucoseValues = ExtractGlucoseValues(hourEntries).ToList();
            var basicStats = CalculateBasicStats(glucoseValues);

            // Calculate extended 7-range time in range percentages for this hour
            var extendedTir = CalculateExtendedTimeInRange(glucoseValues);

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
                TimeInRange = extendedTir,
            };

            averagedStats.Add(hourlyStats);
        }

        return averagedStats;
    }

    /// <summary>
    /// Calculate extended 7-range time in range percentages
    /// Ranges: &lt;54, 54-63, 63-140, 140-180, 180-200, 200-220, &gt;220
    /// </summary>
    /// <param name="glucoseValues">Collection of glucose values in mg/dL</param>
    /// <returns>Extended time in range percentages</returns>
    private ExtendedTimeInRangePercentages CalculateExtendedTimeInRange(IList<double> glucoseValues)
    {
        if (glucoseValues.Count == 0)
        {
            return new ExtendedTimeInRangePercentages();
        }

        var total = glucoseValues.Count;

        // Count readings in each of the 7 ranges
        var veryLowCount = glucoseValues.Count(v => v < 54);
        var lowCount = glucoseValues.Count(v => v >= 54 && v < 63);
        var normalCount = glucoseValues.Count(v => v >= 63 && v < 140);
        var aboveTargetCount = glucoseValues.Count(v => v >= 140 && v < 180);
        var highCount = glucoseValues.Count(v => v >= 180 && v < 200);
        var veryHighCount = glucoseValues.Count(v => v >= 200);

        return new ExtendedTimeInRangePercentages
        {
            VeryLow = Math.Round((double)veryLowCount / total * 100, 1),
            Low = Math.Round((double)lowCount / total * 100, 1),
            Normal = Math.Round((double)normalCount / total * 100, 1),
            AboveTarget = Math.Round((double)aboveTargetCount / total * 100, 1),
            High = Math.Round((double)highCount / total * 100, 1),
            VeryHigh = Math.Round((double)veryHighCount / total * 100, 1),
        };
    }

    #endregion

    #region Treatment Statistics

    /// <summary>
    /// Calculate treatment summary from v4 bolus and carb intake collections
    /// </summary>
    /// <param name="boluses">Collection of boluses</param>
    /// <param name="carbIntakes">Collection of carb intakes</param>
    /// <returns>Treatment summary with totals and counts</returns>
    public TreatmentSummary CalculateTreatmentSummary(
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes
    )
    {
        var summary = new TreatmentSummary
        {
            Totals = new TreatmentTotals { Food = new FoodTotals(), Insulin = new InsulinTotals() },
            TreatmentCount = 0,
        };

        // Aggregate insulin from boluses (all boluses are bolus insulin; basal comes from StateSpans)
        foreach (var bolus in boluses)
        {
            summary.TreatmentCount++;
            summary.Totals.Insulin.Bolus += bolus.Insulin;
        }

        // Aggregate macronutrients from carb intakes
        foreach (var carbIntake in carbIntakes)
        {
            summary.TreatmentCount++;

            summary.Totals.Food.Carbs += carbIntake.Carbs;
            if (carbIntake.Protein.HasValue)
                summary.Totals.Food.Protein += carbIntake.Protein.Value;
            if (carbIntake.Fat.HasValue)
                summary.Totals.Food.Fat += carbIntake.Fat.Value;
        }

        // Calculate carb to insulin ratio
        var totalInsulin = summary.Totals.Insulin.Bolus + summary.Totals.Insulin.Basal;
        summary.CarbToInsulinRatio =
            totalInsulin > 0 ? Math.Round(summary.Totals.Food.Carbs / totalInsulin * 10) / 10 : 0;

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
                        + day.TimeInRanges.Percentages.TightTarget,
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
    /// Determine if a legacy treatment is a bolus based on event type.
    /// Kept for legacy ValidateTreatmentData/CleanTreatmentData support.
    /// </summary>
    /// <param name="treatment">Treatment to check</param>
    /// <returns>True if the treatment is a bolus type</returns>
    private bool IsBolusTreatment(Treatment treatment)
    {
        return BolusTreatmentTypes.Contains(treatment.EventType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Internal building block: calculates bolus-only stats.
    /// Called by the public StateSpan-inclusive overload which adds basal data on top.
    /// </summary>
    private InsulinDeliveryStatistics CalculateBolusDeliveryStatistics(
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        DateTime startDate,
        DateTime endDate
    )
    {
        var bolusList = boluses.ToList();
        var carbList = carbIntakes.ToList();

        // Calculate day count (minimum 1 to avoid division by zero)
        var dayCount = Math.Max(1, (int)Math.Round((endDate - startDate).TotalDays));

        // All Bolus records are bolus insulin; basal comes from StateSpans.
        // This overload only has bolus data, so basal stats will be 0.
        // Use the StateSpan overload for complete basal/bolus analysis.
        double totalBolus = 0;
        int bolusCount = 0;
        int correctionBoluses = 0;
        int mealBoluses = 0;
        int carbBolusCount = 0;

        // Build a set of bolus Mills that are within 15 minutes of a carb entry
        // to classify meal vs correction boluses
        const long mealWindowMs = 15 * 60 * 1000; // 15 minutes
        var carbTimestamps = carbList.Select(c => c.Mills).ToList();

        foreach (var bolus in bolusList)
        {
            if (bolus.Insulin <= 0)
                continue;

            totalBolus += bolus.Insulin;
            bolusCount++;

            // Check if this bolus is linked to a carb entry by CorrelationId or time proximity
            bool isMealBolus = false;

            if (
                bolus.CorrelationId.HasValue
                && carbList.Any(c => c.CorrelationId == bolus.CorrelationId)
            )
            {
                isMealBolus = true;
            }
            else if (carbTimestamps.Any(ct => Math.Abs(bolus.Mills - ct) <= mealWindowMs))
            {
                isMealBolus = true;
            }

            if (isMealBolus)
            {
                mealBoluses++;
                carbBolusCount++;
            }
            else if (!bolus.Automatic)
            {
                correctionBoluses++;
            }
        }

        // Calculate carb totals
        double totalCarbs = carbList.Sum(c => c.Carbs);
        int carbCount = carbList.Count;

        // I:C ratio = grams carbs per unit bolus insulin used for meals
        double icRatio =
            mealBoluses > 0 && totalCarbs > 0
                ? totalCarbs
                    / bolusList
                        .Where(b => b.Insulin > 0)
                        .Where(b =>
                            (
                                b.CorrelationId.HasValue
                                && carbList.Any(c => c.CorrelationId == b.CorrelationId)
                            ) || carbTimestamps.Any(ct => Math.Abs(b.Mills - ct) <= mealWindowMs)
                        )
                        .Sum(b => b.Insulin)
                : 0;

        // Without StateSpans, we can only report bolus data
        var bolusesPerDay = (double)bolusCount / dayCount;

        return new InsulinDeliveryStatistics
        {
            TotalBolus = Math.Round(totalBolus * 100) / 100,
            TotalBasal = 0, // Basal requires StateSpans
            TotalInsulin = Math.Round(totalBolus * 100) / 100,
            TotalCarbs = Math.Round(totalCarbs * 10) / 10,
            BolusCount = bolusCount,
            BasalCount = 0,
            BasalPercent = 0,
            BolusPercent = totalBolus > 0 ? 100 : 0,
            Tdd = Math.Round(totalBolus / dayCount * 10) / 10,
            AvgBolus = bolusCount > 0 ? Math.Round(totalBolus / bolusCount * 100) / 100 : 0,
            MealBoluses = mealBoluses,
            CorrectionBoluses = correctionBoluses,
            IcRatio = Math.Round(icRatio * 10) / 10,
            BolusesPerDay = Math.Round(bolusesPerDay * 10) / 10,
            DayCount = dayCount,
            StartDate = startDate.ToString("yyyy-MM-dd"),
            EndDate = endDate.ToString("yyyy-MM-dd"),
            CarbCount = carbCount,
            CarbBolusCount = carbBolusCount,
        };
    }

    /// <summary>
    /// Extract the rate (U/hr) from a StateSpan's metadata, handling various stored types.
    /// </summary>
    internal static double GetStateSpanRate(StateSpan span)
    {
        if (span.Metadata?.TryGetValue("rate", out var rateObj) != true)
            return 0;

        return rateObj switch
        {
            System.Text.Json.JsonElement je => je.GetDouble(),
            double d => d,
            _ => Convert.ToDouble(rateObj),
        };
    }

    /// <summary>
    /// Calculate the insulin delivered (units) for a single basal StateSpan.
    /// </summary>
    internal static double GetStateSpanBasalInsulin(StateSpan span)
    {
        var endMills = span.EndMills ?? span.StartMills + (5 * 60 * 1000); // Default 5 min
        var durationHours = (endMills - span.StartMills) / (1000.0 * 60 * 60);
        var rate = GetStateSpanRate(span);
        return rate * durationHours;
    }

    /// <summary>
    /// Sum total basal insulin delivered across a collection of BasalDelivery StateSpans.
    /// </summary>
    public double SumBasalFromStateSpans(IEnumerable<StateSpan> basalStateSpans)
    {
        double total = 0;
        foreach (var span in basalStateSpans)
        {
            if (span.Category != StateSpanCategory.BasalDelivery)
                continue;
            var insulin = GetStateSpanBasalInsulin(span);
            if (insulin > 0)
                total += insulin;
        }
        return total;
    }

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics using StateSpans for basal data.
    /// </summary>
    public InsulinDeliveryStatistics CalculateInsulinDeliveryStatistics(
        IEnumerable<Bolus> boluses,
        IEnumerable<StateSpan> basalStateSpans,
        IEnumerable<CarbIntake> carbIntakes,
        DateTime startDate,
        DateTime endDate
    )
    {
        // Start with bolus-based calculation (includes carb stats)
        var stats = CalculateBolusDeliveryStatistics(boluses, carbIntakes, startDate, endDate);

        // Replace basal with StateSpan-derived total
        var stateSpanBasal = SumBasalFromStateSpans(basalStateSpans);
        var totalInsulin = stats.TotalBolus + stateSpanBasal;

        stats.TotalBasal = Math.Round(stateSpanBasal * 100) / 100;
        stats.TotalInsulin = Math.Round(totalInsulin * 100) / 100;
        stats.Tdd = Math.Round(totalInsulin / Math.Max(1, stats.DayCount) * 10) / 10;
        stats.BasalPercent =
            totalInsulin > 0 ? Math.Round(stateSpanBasal / totalInsulin * 100 * 10) / 10 : 0;
        stats.BolusPercent =
            totalInsulin > 0 ? Math.Round(stats.TotalBolus / totalInsulin * 100 * 10) / 10 : 0;

        return stats;
    }

    /// <summary>
    /// Calculate daily basal/bolus ratio breakdown using StateSpans for basal data
    /// </summary>
    public DailyBasalBolusRatioResponse CalculateDailyBasalBolusRatios(
        IEnumerable<Bolus> boluses,
        IEnumerable<StateSpan> basalStateSpans
    )
    {
        var bolusList = boluses.ToList();
        var basalSpansList = basalStateSpans.ToList();
        var dailyData = new Dictionary<string, (double Basal, double Bolus)>();

        // Process boluses (all Bolus records are bolus insulin; basal comes from StateSpans)
        foreach (var bolus in bolusList)
        {
            if (bolus.Insulin <= 0 || bolus.Mills <= 0)
                continue;

            var date = DateTimeOffset.FromUnixTimeMilliseconds(bolus.Mills).DateTime;
            var dateKey = date.ToString("yyyy-MM-dd");
            if (!dailyData.ContainsKey(dateKey))
                dailyData[dateKey] = (0, 0);

            var (currentBasal, currentBolus) = dailyData[dateKey];
            dailyData[dateKey] = (currentBasal, currentBolus + bolus.Insulin);
        }

        // Process basal StateSpans
        foreach (var span in basalSpansList)
        {
            if (span.Category != StateSpanCategory.BasalDelivery)
                continue;

            var basalInsulin = GetStateSpanBasalInsulin(span);
            if (basalInsulin <= 0)
                continue;

            var spanDate = DateTimeOffset.FromUnixTimeMilliseconds(span.StartMills).DateTime;
            var dateKey = spanDate.ToString("yyyy-MM-dd");
            if (!dailyData.ContainsKey(dateKey))
                dailyData[dateKey] = (0, 0);

            var (currentBasal, currentBolus) = dailyData[dateKey];
            dailyData[dateKey] = (currentBasal + basalInsulin, currentBolus);
        }

        // Build response
        var sortedDates = dailyData.Keys.OrderBy(d => d).ToList();
        var result = new DailyBasalBolusRatioResponse
        {
            DailyData = new List<DailyBasalBolusRatioData>(),
            DayCount = sortedDates.Count,
        };

        double totalBasal = 0;
        double totalBolus = 0;

        foreach (var dateKey in sortedDates)
        {
            var (basal, bolus) = dailyData[dateKey];
            var total = basal + bolus;
            var basalPercent = total > 0 ? (basal / total) * 100 : 0;
            var bolusPercent = total > 0 ? (bolus / total) * 100 : 0;

            var dateParsed = DateTime.Parse(dateKey);
            var displayDate = dateParsed.ToString("MMM d");

            result.DailyData.Add(
                new DailyBasalBolusRatioData
                {
                    Date = dateKey,
                    DisplayDate = displayDate,
                    Basal = Math.Round(basal * 100) / 100,
                    Bolus = Math.Round(bolus * 100) / 100,
                    Total = Math.Round(total * 100) / 100,
                    BasalPercent = Math.Round(basalPercent * 10) / 10,
                    BolusPercent = Math.Round(bolusPercent * 10) / 10,
                }
            );

            totalBasal += basal;
            totalBolus += bolus;
        }

        var grandTotal = totalBasal + totalBolus;
        result.AverageBasalPercent =
            grandTotal > 0 ? Math.Round((totalBasal / grandTotal) * 100 * 10) / 10 : 0;
        result.AverageBolusPercent =
            grandTotal > 0 ? Math.Round((totalBolus / grandTotal) * 100 * 10) / 10 : 0;
        result.AverageTdd =
            result.DayCount > 0 ? Math.Round((grandTotal / result.DayCount) * 10) / 10 : 0;

        return result;
    }

    /// <summary>
    /// Calculate comprehensive basal analysis statistics using StateSpans
    /// </summary>
    public BasalAnalysisResponse CalculateBasalAnalysis(
        IEnumerable<StateSpan> basalStateSpans,
        DateTime startDate,
        DateTime endDate
    )
    {
        var spansList = basalStateSpans
            .Where(s => s.Category == StateSpanCategory.BasalDelivery)
            .ToList();
        var dayCount = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalDays));

        // Track stats
        var allRates = new List<double>();
        double totalDelivered = 0;
        int tempBasalCount = 0;
        int highTempCount = 0;
        int lowTempCount = 0;
        int zeroTempCount = 0;

        // Hourly rate buckets
        var hourlyRates = new Dictionary<int, List<double>>();
        for (int h = 0; h < 24; h++)
            hourlyRates[h] = new List<double>();

        foreach (var span in spansList)
        {
            var rate = GetStateSpanRate(span);

            // Get origin and scheduledRate from metadata (specific to basal analysis)
            string? origin = null;
            double? scheduledRate = null;
            if (span.Metadata != null)
            {
                if (span.Metadata.TryGetValue("origin", out var originObj))
                {
                    origin = originObj?.ToString();
                }
                if (span.Metadata.TryGetValue("scheduledRate", out var schedObj))
                {
                    scheduledRate = schedObj switch
                    {
                        System.Text.Json.JsonElement je => je.GetDouble(),
                        double d => d,
                        _ => Convert.ToDouble(schedObj),
                    };
                }
            }

            var startTime = DateTimeOffset.FromUnixTimeMilliseconds(span.StartMills).DateTime;

            allRates.Add(rate);
            totalDelivered += GetStateSpanBasalInsulin(span);

            // Add to hourly buckets
            var hour = startTime.Hour;
            hourlyRates[hour].Add(rate);

            // Track temp basals (non-scheduled origins)
            if (
                origin != null
                && !origin.Equals("Scheduled", StringComparison.OrdinalIgnoreCase)
                && !origin.Equals("Inferred", StringComparison.OrdinalIgnoreCase)
            )
            {
                tempBasalCount++;

                if (rate == 0 || origin.Equals("Suspended", StringComparison.OrdinalIgnoreCase))
                {
                    zeroTempCount++;
                }
                else if (scheduledRate.HasValue)
                {
                    if (rate > scheduledRate.Value)
                        highTempCount++;
                    else if (rate < scheduledRate.Value)
                        lowTempCount++;
                }
            }
        }

        var basalStats = new BasalStats
        {
            Count = spansList.Count,
            AvgRate = allRates.Count > 0 ? Math.Round(allRates.Average() * 100) / 100 : 0,
            MinRate = allRates.Count > 0 ? Math.Round(allRates.Min() * 100) / 100 : 0,
            MaxRate = allRates.Count > 0 ? Math.Round(allRates.Max() * 100) / 100 : 0,
            TotalDelivered = Math.Round(totalDelivered * 100) / 100,
        };

        var tempBasalInfo = new TempBasalInfo
        {
            Total = tempBasalCount,
            PerDay = dayCount > 0 ? Math.Round((tempBasalCount / (double)dayCount) * 10) / 10 : 0,
            HighTemps = highTempCount,
            LowTemps = lowTempCount,
            ZeroTemps = zeroTempCount,
        };

        var hourlyPercentiles = new List<HourlyBasalPercentileData>();
        for (int hour = 0; hour < 24; hour++)
        {
            var hourRates = hourlyRates[hour];
            if (hourRates.Count > 0)
            {
                hourlyPercentiles.Add(
                    new HourlyBasalPercentileData
                    {
                        Hour = hour,
                        P10 = Math.Round(CalculatePercentile(hourRates, 10) * 100) / 100,
                        P25 = Math.Round(CalculatePercentile(hourRates, 25) * 100) / 100,
                        Median = Math.Round(CalculatePercentile(hourRates, 50) * 100) / 100,
                        P75 = Math.Round(CalculatePercentile(hourRates, 75) * 100) / 100,
                        P90 = Math.Round(CalculatePercentile(hourRates, 90) * 100) / 100,
                        Count = hourRates.Count,
                    }
                );
            }
            else
            {
                hourlyPercentiles.Add(
                    new HourlyBasalPercentileData
                    {
                        Hour = hour,
                        P10 = 0,
                        P25 = 0,
                        Median = 0,
                        P75 = 0,
                        P90 = 0,
                        Count = 0,
                    }
                );
            }
        }

        return new BasalAnalysisResponse
        {
            Stats = basalStats,
            TempBasalInfo = tempBasalInfo,
            HourlyPercentiles = hourlyPercentiles,
            DayCount = dayCount,
            StartDate = startDate.ToString("yyyy-MM-dd"),
            EndDate = endDate.ToString("yyyy-MM-dd"),
        };
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
    /// <param name="boluses">Collection of boluses</param>
    /// <param name="carbIntakes">Collection of carb intakes</param>
    /// <param name="config">Extended analysis configuration (optional)</param>
    /// <returns>Comprehensive glucose analytics</returns>
    public GlucoseAnalytics AnalyzeGlucoseData(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
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
            .Where(entry => entry.Mgdl > 0)
            .OrderBy(entry => entry.Mills)
            .ToList();

        var basicStats = CalculateBasicStats(glucoseValues);
        var timeInRange = CalculateTimeInRange(sortedEntries, config.Thresholds);
        var glycemicVariability = CalculateGlycemicVariability(glucoseValues, sortedEntries);
        var dataQuality = AssessDataQuality(sortedEntries);

        var timeStart = sortedEntries.FirstOrDefault()?.Mills ?? 0;
        var timeEnd = sortedEntries.LastOrDefault()?.Mills ?? 0;

        // Calculate reliability from data span
        var daysOfData = sortedEntries
            .Select(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Mills).Date)
            .Distinct()
            .Count();
        var reliability = AssessReliability(daysOfData, sortedEntries.Count);

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
            Reliability = reliability,
        };
    }

    private DataQuality AssessDataQuality(IList<SensorGlucose> entries)
    {
        var totalReadings = entries.Count;
        var gaps = new List<DataGap>();

        if (entries.Count > 1)
        {
            for (int i = 1; i < entries.Count; i++)
            {
                var prevTime = entries[i - 1].Mills;
                var currentTime = entries[i].Mills;
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

    #region Site Change Analysis

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="entries">Glucose entries</param>
    /// <param name="deviceEvents">Device events including site changes</param>
    /// <param name="hoursBeforeChange">Hours before site change to analyze (default: 12)</param>
    /// <param name="hoursAfterChange">Hours after site change to analyze (default: 24)</param>
    /// <param name="bucketSizeMinutes">Time bucket size for averaging (default: 30)</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    public SiteChangeImpactAnalysis CalculateSiteChangeImpact(
        IEnumerable<SensorGlucose> entries,
        IEnumerable<DeviceEvent> deviceEvents,
        int hoursBeforeChange = 12,
        int hoursAfterChange = 24,
        int bucketSizeMinutes = 30
    )
    {
        var result = new SiteChangeImpactAnalysis
        {
            HoursBeforeChange = hoursBeforeChange,
            HoursAfterChange = hoursAfterChange,
            BucketSizeMinutes = bucketSizeMinutes,
        };

        // Filter for site changes and pod changes
        var siteChanges = deviceEvents
            .Where(e =>
                e.EventType == DeviceEventType.SiteChange
                || e.EventType == DeviceEventType.PodChange
                || e.EventType == DeviceEventType.CannulaChange
            )
            .OrderBy(e => e.Mills)
            .ToList();

        result.SiteChangeCount = siteChanges.Count;

        if (siteChanges.Count < 2)
        {
            result.HasSufficientData = false;
            return result;
        }

        // Convert entries to a list with timestamps for efficient lookup
        var entriesList = entries
            .Select(e => new
            {
                Entry = e,
                Mills = e.Mills,
                Glucose = e.Mgdl,
            })
            .Where(e => e.Glucose > 0 && e.Glucose < 600) // Filter invalid readings
            .OrderBy(e => e.Mills)
            .ToList();

        if (entriesList.Count < 100)
        {
            result.HasSufficientData = false;
            return result;
        }

        // Calculate time buckets
        var minutesBefore = hoursBeforeChange * 60;
        var minutesAfter = hoursAfterChange * 60;
        var totalBuckets = (minutesBefore + minutesAfter) / bucketSizeMinutes;

        // Initialize bucket data structure
        var buckets = new Dictionary<int, List<double>>();
        for (int i = 0; i < totalBuckets; i++)
        {
            var minutesFromChange = (i * bucketSizeMinutes) - minutesBefore;
            buckets[minutesFromChange] = new List<double>();
        }

        // For each site change, collect glucose readings into corresponding buckets
        foreach (var siteChange in siteChanges)
        {
            var changeTime = siteChange.Mills;
            if (changeTime == 0)
                continue;

            // Find glucose readings in the window around this site change
            var windowStart = changeTime - (minutesBefore * 60 * 1000); // Convert minutes to milliseconds
            var windowEnd = changeTime + (minutesAfter * 60 * 1000);

            var windowEntries = entriesList
                .Where(e => e.Mills >= windowStart && e.Mills <= windowEnd)
                .ToList();

            foreach (var entry in windowEntries)
            {
                var minutesFromChange = (entry.Mills - changeTime) / (60.0 * 1000.0);

                // Find the appropriate bucket
                var bucketMinutes =
                    ((int)Math.Floor(minutesFromChange / bucketSizeMinutes)) * bucketSizeMinutes;

                // Clamp to valid range
                if (bucketMinutes < -minutesBefore)
                    bucketMinutes = -minutesBefore;
                if (bucketMinutes >= minutesAfter)
                    bucketMinutes = minutesAfter - bucketSizeMinutes;

                if (buckets.ContainsKey(bucketMinutes))
                {
                    buckets[bucketMinutes].Add(entry.Glucose);
                }
            }
        }

        // Calculate statistics for each bucket
        var dataPoints = new List<SiteChangeImpactDataPoint>();
        var beforeValues = new List<double>();
        var afterValues = new List<double>();

        foreach (var kvp in buckets.OrderBy(b => b.Key))
        {
            var minutesFromChange = kvp.Key;
            var values = kvp.Value;

            if (values.Count == 0)
                continue;

            var sorted = values.OrderBy(v => v).ToList();
            var mean = values.Average();
            var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
            var stdDev = Math.Sqrt(variance);

            dataPoints.Add(
                new SiteChangeImpactDataPoint
                {
                    MinutesFromChange = minutesFromChange,
                    AverageGlucose = Math.Round(mean, 1),
                    MedianGlucose = Math.Round(CalculatePercentile(sorted, 50), 1),
                    StdDev = Math.Round(stdDev, 1),
                    Count = values.Count,
                    Percentile10 = Math.Round(CalculatePercentile(sorted, 10), 1),
                    Percentile25 = Math.Round(CalculatePercentile(sorted, 25), 1),
                    Percentile75 = Math.Round(CalculatePercentile(sorted, 75), 1),
                    Percentile90 = Math.Round(CalculatePercentile(sorted, 90), 1),
                }
            );

            // Collect values for before/after summary
            if (minutesFromChange < 0)
            {
                beforeValues.AddRange(values);
            }
            else
            {
                afterValues.AddRange(values);
            }
        }

        result.DataPoints = dataPoints;
        result.HasSufficientData =
            dataPoints.Count >= 10 && beforeValues.Count >= 50 && afterValues.Count >= 50;

        if (!result.HasSufficientData)
        {
            // Insufficient data flag already set
        }

        // Calculate summary statistics
        if (beforeValues.Count > 0 && afterValues.Count > 0)
        {
            var avgBefore = beforeValues.Average();
            var avgAfter = afterValues.Average();

            // Calculate time in range
            var tirBefore =
                (double)beforeValues.Count(v => v >= 70 && v <= 180) / beforeValues.Count * 100;
            var tirAfter =
                (double)afterValues.Count(v => v >= 70 && v <= 180) / afterValues.Count * 100;

            // Calculate CV (coefficient of variation)
            var stdDevBefore = Math.Sqrt(
                beforeValues.Sum(v => Math.Pow(v - avgBefore, 2)) / beforeValues.Count
            );
            var stdDevAfter = Math.Sqrt(
                afterValues.Sum(v => Math.Pow(v - avgAfter, 2)) / afterValues.Count
            );
            var cvBefore = avgBefore > 0 ? (stdDevBefore / avgBefore) * 100 : 0;
            var cvAfter = avgAfter > 0 ? (stdDevAfter / avgAfter) * 100 : 0;

            result.Summary = new SiteChangeImpactSummary
            {
                AvgGlucoseBeforeChange = Math.Round(avgBefore, 1),
                AvgGlucoseAfterChange = Math.Round(avgAfter, 1),
                PercentImprovement =
                    avgBefore > 0 ? Math.Round((avgBefore - avgAfter) / avgBefore * 100, 1) : 0,
                TimeInRangeBeforeChange = Math.Round(tirBefore, 1),
                TimeInRangeAfterChange = Math.Round(tirAfter, 1),
                CvBeforeChange = Math.Round(cvBefore, 1),
                CvAfterChange = Math.Round(cvAfter, 1),
            };
        }

        return result;
    }

    #endregion
}
