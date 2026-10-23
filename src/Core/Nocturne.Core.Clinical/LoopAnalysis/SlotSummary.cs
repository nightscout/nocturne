using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>Summarises the values behind one series in one slot.</summary>
public static class SlotSummary
{
    /// <summary>
    /// Days a slot needs before its spread is read as a pattern rather than as a handful of days.
    /// </summary>
    public const int SparseDayThreshold = 5;

    /// <summary>
    /// At least this share of a slot must hold a value for the slot to count on a day; less, and
    /// the slot is absent that day rather than averaged over a sliver.
    /// </summary>
    public const double MinimumSlotCoverage = 0.5;

    private const int Decimals = 3;

    /// <returns>Null when there are no values: absent, never zero.</returns>
    /// <param name="days">Days the values came from, for <see cref="LoopAnalysisAggregate.Sparse"/>.</param>
    public static LoopAnalysisAggregate? Of(List<double> values, int days)
    {
        if (values.Count == 0)
            return null;

        values.Sort();
        return new LoopAnalysisAggregate
        {
            P10 = Round(GlucoseStatistics.Percentile(values, 10)),
            P25 = Round(GlucoseStatistics.Percentile(values, 25)),
            P50 = Round(GlucoseStatistics.Percentile(values, 50)),
            P75 = Round(GlucoseStatistics.Percentile(values, 75)),
            P90 = Round(GlucoseStatistics.Percentile(values, 90)),
            Mean = Round(values.Average()),
            N = values.Count,
            Sparse = days < SparseDayThreshold,
        };
    }

    public static LoopAnalysisAggregate? Of(List<double> perDayValues) => Of(perDayValues, perDayValues.Count);

    public static double Round(double value) => Math.Round(value, Decimals);
}
