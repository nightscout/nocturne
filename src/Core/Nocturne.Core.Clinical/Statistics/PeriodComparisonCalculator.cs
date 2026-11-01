using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Statistics;

/// <summary>
/// Builds a <see cref="PeriodComparison"/> from the analysis and insulin delivery of two periods.
/// Owns each metric's display precision, so the change a client shows is rounded where the figures
/// themselves are, and whether a metric moved is decided here.
/// </summary>
public static class PeriodComparisonCalculator
{
    /// <summary>
    /// A period's analytics and insulin delivery. <paramref name="Analysis"/> is the range
    /// analytics result, whose zeros for an empty window <see cref="Build"/> reads as no data.
    /// </summary>
    public readonly record struct PeriodFigures(ReportAnalysisResult Analysis, InsulinDeliveryStatistics Insulin);

    private readonly record struct Precision(int Value, int Delta)
    {
        public static implicit operator Precision(int digits) => new(digits, digits);
    }

    private readonly record struct Metric(
        PeriodComparisonMetric Key,
        Func<PeriodFigures, double?> Pick,
        Precision Precision,
        bool IsGlucose = false);

    private static readonly Precision MgDl = 0;
    private static readonly Precision Mmol = 1;

    private static readonly IReadOnlyList<Metric> Metrics =
    [
        new(PeriodComparisonMetric.TirTarget, p => p.Analysis.Analysis.TimeInRange.Percentages.Target, 1),
        new(PeriodComparisonMetric.Gmi, p => p.Analysis.Analysis.GMI.Value, new Precision(1, 2)),
        new(PeriodComparisonMetric.Cv, p => p.Analysis.Analysis.GlycemicVariability?.CoefficientOfVariation, 1),
        new(PeriodComparisonMetric.Gri, p => p.Analysis.Analysis.GRI.Score, 0),
        new(PeriodComparisonMetric.Mean, p => p.Analysis.Analysis.BasicStats.Mean, MgDl, IsGlucose: true),
        new(PeriodComparisonMetric.HyperMinutesPerDay, p => p.Analysis.Analysis.TimeInRange.AverageDailyMinutes.High, 0),
        new(PeriodComparisonMetric.HyperEventsPerDay, p => p.Analysis.Analysis.TimeInRange.AverageDailyEpisodes.AboveRange, 2),
        new(PeriodComparisonMetric.HypoMinutesPerDay, p => p.Analysis.Analysis.TimeInRange.AverageDailyMinutes.Low, 0),
        new(PeriodComparisonMetric.HypoEventsPerDay, p => p.Analysis.Analysis.TimeInRange.AverageDailyEpisodes.BelowRange, 2),

        new(PeriodComparisonMetric.VeryLowPercent, p => p.Analysis.Analysis.TimeInRange.Percentages.VeryLow, 1),
        new(PeriodComparisonMetric.LowPercent, p => p.Analysis.Analysis.TimeInRange.Percentages.Low, 1),
        new(PeriodComparisonMetric.HighPercent, p => p.Analysis.Analysis.TimeInRange.Percentages.High, 1),
        new(PeriodComparisonMetric.VeryHighPercent, p => p.Analysis.Analysis.TimeInRange.Percentages.VeryHigh, 1),
        new(PeriodComparisonMetric.PersonalRangePercent, p => p.Analysis.PersonalRange?.InRangePercent, 1),

        new(PeriodComparisonMetric.Sd, p => p.Analysis.Analysis.GlycemicVariability?.StandardDeviation, MgDl, IsGlucose: true),
        new(PeriodComparisonMetric.Median, p => p.Analysis.Analysis.BasicStats.Median, MgDl, IsGlucose: true),
        new(PeriodComparisonMetric.Lbgi, p => p.Analysis.Analysis.GlycemicVariability?.LowBloodGlucoseIndex, 1),
        new(PeriodComparisonMetric.Hbgi, p => p.Analysis.Analysis.GlycemicVariability?.HighBloodGlucoseIndex, 1),
        new(PeriodComparisonMetric.Mage, p => p.Analysis.Analysis.GlycemicVariability?.MeanAmplitudeGlycemicExcursions, MgDl, IsGlucose: true),
        new(PeriodComparisonMetric.Conga, p => p.Analysis.Analysis.GlycemicVariability?.ContinuousOverlappingNetGlycemicAction, MgDl, IsGlucose: true),
        new(PeriodComparisonMetric.Adrr, p => p.Analysis.Analysis.GlycemicVariability?.AverageDailyRiskRange, 1),

        new(PeriodComparisonMetric.InsulinPerDay, p => WhenInsulin(p, i => i.Tdd), 1),
        new(PeriodComparisonMetric.BasalPerDay, p => WhenInsulin(p, i => i.TotalBasalPerDay), 1),
        new(PeriodComparisonMetric.BasalPercent, p => WhenInsulin(p, i => i.BasalPercent), 1),
        new(PeriodComparisonMetric.BolusesPerDay, p => WhenInsulin(p, i => i.BolusesPerDay), 1),
        new(PeriodComparisonMetric.CarbsPerDay, p => p.Insulin.CarbCount > 0 ? p.Insulin.CarbsPerDay : null, 0),
        new(PeriodComparisonMetric.Icr, p => p.Insulin.IcRatio, 2),

        new(PeriodComparisonMetric.CgmActivePercent, p => p.Analysis.Analysis.DataQuality.CgmActivePercent, 1),
        new(PeriodComparisonMetric.DaysWithData, p => p.Analysis.Analysis.DataSufficiency.DaysWithData, 0),
        new(PeriodComparisonMetric.ReadingsPerDay, p => p.Analysis.Analysis.DataSufficiency.AverageReadingsPerDay, 0),
    ];

    /// <param name="a">The baseline period.</param>
    /// <param name="b">The period compared against it.</param>
    /// <param name="units">The units glucose metrics are served in. Everything is computed in mg/dL and converted for display.</param>
    public static PeriodComparison Build(PeriodFigures a, PeriodFigures b, GlucoseUnit units)
    {
        var hasDataA = HasReadings(a);
        var hasDataB = HasReadings(b);

        var comparison = new PeriodComparison
        {
            Units = units,
            PeriodA = Period(a, hasDataA),
            PeriodB = Period(b, hasDataB),
            TimeZoneUnavailableReason = a.Analysis.TimeZoneUnavailableReason ?? b.Analysis.TimeZoneUnavailableReason,
        };

        foreach (var metric in Metrics)
        {
            var valueA = hasDataA ? metric.Pick(a) : null;
            var valueB = hasDataB ? metric.Pick(b) : null;
            if (metric.Key == PeriodComparisonMetric.PersonalRangePercent && valueA is null && valueB is null)
                continue;

            comparison.Metrics.Add(Compare(metric, valueA, valueB, units));
        }

        return comparison;
    }

    private static bool HasReadings(PeriodFigures period) => period.Analysis.Analysis.BasicStats.Count > 0;

    private static ComparedPeriod Period(PeriodFigures period, bool hasData) => new()
    {
        HasData = hasData,
        TimeInRange = hasData ? period.Analysis.Analysis.TimeInRange.Percentages : null,
    };

    private static double? WhenInsulin(PeriodFigures period, Func<InsulinDeliveryStatistics, double> pick) =>
        period.Insulin.InsulinEventCount > 0 ? pick(period.Insulin) : null;

    private static MetricComparison Compare(Metric metric, double? valueA, double? valueB, GlucoseUnit units)
    {
        var inMmol = metric.IsGlucose && units == GlucoseUnit.Mmol;
        var precision = inMmol ? Mmol : metric.Precision;
        double Display(double value) => inMmol ? value / GlucoseConstants.MgdlPerMmol : value;

        var comparison = new MetricComparison
        {
            Metric = metric.Key,
            ValueA = valueA is { } a ? Round(Display(a), precision.Value) : null,
            ValueB = valueB is { } b ? Round(Display(b), precision.Value) : null,
            ValueDecimals = precision.Value,
            DeltaDecimals = precision.Delta,
        };

        if (valueA is not { } from || valueB is not { } to)
            return comparison;

        comparison.Delta = Round(Display(to - from), precision.Delta);
        comparison.PercentChange = from == 0 ? null : (to - from) / Math.Abs(from) * 100;
        return comparison;
    }

    private static double Round(double value, int decimals)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}
