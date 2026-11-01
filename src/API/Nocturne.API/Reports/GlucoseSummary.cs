using System.Globalization;
using System.Text;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports;

/// <summary>
/// The full text of a glucose series panel: every figure the chart draws, in words. The figures come from
/// <see cref="IStatisticsService"/>, the calculators the reports use.
/// </summary>
internal static class GlucoseSummary
{
    /// <summary>
    /// The chart's cut-points as the statistics calculators take them: the clinical consensus limits
    /// (54, 70, 180, 250 mg/dL) that <c>ChartThresholdsBuilder</c> sets for every tenant, not the profile
    /// target, which the chart carries apart as <see cref="ChartThresholdsDto.TargetLow"/>/<see cref="ChartThresholdsDto.TargetHigh"/>.
    /// </summary>
    public static GlycemicThresholds Thresholds(ChartThresholdsDto chart)
    {
        var defaults = new GlycemicThresholds();
        double Or(double value, double fallback) => value > 0 ? value : fallback;
        var low = Or(chart.Low, defaults.Low);
        var high = Or(chart.High, defaults.High);
        return new GlycemicThresholds
        {
            VeryLow = Or(chart.VeryLow, defaults.VeryLow),
            Low = low,
            TargetBottom = low,
            TargetTop = high,
            High = high,
            VeryHigh = Or(chart.VeryHigh, defaults.VeryHigh),
        };
    }

    /// <summary>
    /// The text for <paramref name="data"/>'s glucose. <paramref name="omittedFields"/> names the
    /// fields left out because the token cannot read them.
    /// </summary>
    public static string Build(
        DashboardChartData data,
        IStatisticsService statistics,
        bool useMmol,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string>? omittedFields = null)
    {
        var text = new StringBuilder();
        var units = useMmol ? "mmol/L" : "mg/dL";

        text.Append(CultureInfo.InvariantCulture,
            $"Glucose from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC");
        text.Append(".").Append('\n');

        var basic = statistics.CalculateBasicStats(data.GlucoseData.Select(r => r.Sgv));
        if (basic.Count == 0)
        {
            text.Append("No glucose readings in this period.").Append('\n');
        }
        else
        {
            var thresholds = Thresholds(data.Thresholds);
            var tir = statistics.CalculateTimeInRange(Readings(data), thresholds);
            var below = tir.Percentages.VeryLow + tir.Percentages.Low;
            var above = tir.Percentages.High + tir.Percentages.VeryHigh;

            text.Append(CultureInfo.InvariantCulture, $"Readings: {basic.Count}.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Min {Format(basic.Min, useMmol)}, max {Format(basic.Max, useMmol)}, "
                + $"mean {Format(basic.Mean, useMmol)} {units}.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Time in range ({Format(thresholds.TargetBottom, useMmol)}-{Format(thresholds.TargetTop, useMmol)} {units}): "
                + $"{Percent(tir.Percentages.Target)}%. Below: {Percent(below)}%. Above: {Percent(above)}%.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Lows (episodes below range): {tir.Episodes.BelowRange}. Highs (episodes above range): {tir.Episodes.AboveRange}.")
                .Append('\n');
        }

        if (omittedFields is { Count: > 0 })
            text.Append($"Fields omitted: {string.Join(", ", omittedFields)} (scope not granted).").Append('\n');

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The figures a glucose panel prints (time in range, below, above, mean, lowest, highest), its
    /// sentence (the episodes) and its range line.
    /// </summary>
    public static (IReadOnlyList<Reports.ReportFact> Facts, string Sentence, string Context) Facts(
        DashboardChartData data, IStatisticsService statistics, bool useMmol, DateTimeOffset from, DateTimeOffset to,
        TimeZoneInfo? zone = null)
    {
        var basic = statistics.CalculateBasicStats(data.GlucoseData.Select(r => r.Sgv));
        var context = $"{Reports.ReportFacts.Window(from, to, zone)}. {Reports.ReportFacts.Plural(basic.Count, "reading")}.";
        if (basic.Count == 0)
            return ([], "No glucose readings in this period.", context);

        var thresholds = Thresholds(data.Thresholds);
        var tir = statistics.CalculateTimeInRange(Readings(data), thresholds);
        var range = $"{Format(thresholds.TargetBottom, useMmol)}-{Format(thresholds.TargetTop, useMmol)} {Reports.ReportFacts.GlucoseUnit(useMmol)}";
        Reports.ReportFact[] facts =
        [
            Reports.ReportFacts.Percent("In range", Math.Round(tir.Percentages.Target), range),
            Reports.ReportFacts.Percent("Below range", Math.Round(tir.Percentages.VeryLow + tir.Percentages.Low)),
            Reports.ReportFacts.Percent("Above range", Math.Round(tir.Percentages.High + tir.Percentages.VeryHigh)),
            Reports.ReportFacts.Glucose("Mean", basic.Mean, useMmol),
            Reports.ReportFacts.Glucose("Lowest", basic.Min, useMmol),
            Reports.ReportFacts.Glucose("Highest", basic.Max, useMmol),
        ];
        var sentence = $"{Reports.ReportFacts.Plural(tir.Episodes.BelowRange, "episode")} below range and {tir.Episodes.AboveRange} above it.";
        return (facts, sentence, context);
    }

    /// <summary>The chart's readings as the calculators read them.</summary>
    internal static IEnumerable<SensorGlucose> Readings(DashboardChartData data) =>
        data.GlucoseData.Select(r => new SensorGlucose
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Time).UtcDateTime,
            Mgdl = r.Sgv,
        });

    internal static string Format(double mgdl, bool useMmol) =>
        GlucoseUnits.Scale(mgdl, useMmol).ToString(useMmol ? "0.0" : "0", CultureInfo.InvariantCulture);

    private static string Percent(double percent) =>
        Math.Round(percent).ToString("0", CultureInfo.InvariantCulture);
}
