using System.Diagnostics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Performance.Tests;

/// <summary>
/// Prediction accuracy over the widest range it accepts, 90 days of oref snapshots every five
/// minutes with all four curves (the most pairs a snapshot yields), parsed from their stored JSON,
/// and a comparison range of the same size.
/// </summary>
public class LoopAnalysisPredictionAccuracyBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private const long Minute = 60_000;
    private const long Day = 24 * 60 * Minute;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    [Trait("Category", "Performance")]
    public void NinetyDaysOfFiveMinuteSnapshots_ComputesWithinBudget()
    {
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();
        var from = to - 90 * Day;
        var compareFrom = from - 90 * Day;

        string Curve(long t, int slope) =>
            "[" + string.Join(",", Enumerable.Range(0, 48).Select(i => 100 + (t / (5 * Minute) + i * slope) % 120)) + "]";

        var points = new List<ApsPredictionPoint>();
        var glucose = new List<GlucoseSample>();
        var carbs = new List<long>();
        var boluses = new List<long>();
        for (var t = compareFrom; t < to + 3 * 60 * Minute; t += 5 * Minute)
        {
            glucose.Add(new GlucoseSample(t, 90 + t / (5 * Minute) % 120));
            if (t >= to)
                continue;
            points.Add(new ApsPredictionPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime, "openaps://phone", true, AidAlgorithm.AndroidAps,
                Curve(t, 1), Curve(t, 1), Curve(t, -1), Curve(t, 2), Curve(t, 3)));
            if (t % (8 * 60 * Minute) == 0)
            {
                carbs.Add(t);
                boluses.Add(t);
            }
            else if (t % (5 * 60 * Minute) == 0)
            {
                boluses.Add(t);
            }
        }

        var stopwatch = Stopwatch.StartNew();

        List<PredictionSnapshot> snapshots = [.. points.Select(PredictionAccuracyCalculator.Parse).OfType<PredictionSnapshot>()];
        var parsed = stopwatch.Elapsed;
        var inputs = new PredictionAccuracyInputs
        {
            Zone = Zone,
            Snapshots = snapshots,
            Glucose = glucose,
            CarbMills = carbs,
            ManualBolusMills = boluses,
        };
        var filter = new LoopAnalysisMasks();
        var sources = new LoopAnalysisMaskSources();
        var primary = PredictionAccuracyCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, from, to, Zone, sources), from, to);
        var comparison = PredictionAccuracyCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, compareFrom, from, Zone, sources), compareFrom, from);

        stopwatch.Stop();
        output.WriteLine($"180 days ({points.Count} snapshots, {glucose.Count} readings): {stopwatch.Elapsed.TotalMilliseconds:F0} ms, {parsed.TotalMilliseconds:F0} ms of it parsing");

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new PredictionAccuracyResponse { Primary = primary, Comparison = comparison },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        output.WriteLine($"payload: {payload.Length} bytes");

        Assert.Equal(90 * 288, primary.Predictions);
        Assert.Equal(90 * 288, comparison.Predictions);
        Assert.Equal(4, primary.Curves.Count);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}, budget {Budget}");
    }
}
