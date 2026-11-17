using System.Diagnostics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Performance.Tests;

/// <summary>
/// Loop health's compute over the widest range it accepts, 90 days of five-minute oref loop cycles
/// with a temp and a reading every cycle and frequent microboluses, and a comparison range of the
/// same size.
/// </summary>
public class LoopAnalysisLoopHealthBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private const long Minute = 60_000;
    private const long Day = 24 * 60 * Minute;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    [Trait("Category", "Performance")]
    public void NinetyDaysOfFiveMinuteCycles_ComputeWithinBudget()
    {
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();
        var from = to - 90 * Day;
        var compareFrom = from - 90 * Day;

        var cycles = new List<ApsLoopCycle>();
        var temps = new List<TempBasal>();
        var doses = new List<InsulinDose>();
        var glucose = new List<SensorGlucose>();
        for (var t = compareFrom; t < to; t += 5 * Minute)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            var step = t / (5 * Minute);
            var rate = 0.4 + step % 7 * 0.2;
            var id = Guid.NewGuid();
            cycles.Add(new ApsLoopCycle
            {
                Id = id,
                Timestamp = at, AidAlgorithm = AidAlgorithm.Trio, Enacted = step % 50 != 0, EnactedRate = rate,
                Iob = step % 40 * 0.1, SensitivityRatio = 0.8 + step % 5 * 0.1,
                HasReason = true, SuggestedChange = true,
                LimitReason = step % 30 == 0 ? $"adj. req. rate: 3.1 to maxSafeBasal: {rate}" : null,
            });
            temps.Add(new TempBasal
            {
                StartTimestamp = at, EndTimestamp = at.AddMinutes(30), Rate = rate,
                Origin = TempBasalOrigin.Algorithm, OriginEvidence = TempBasalOriginEvidence.Snapshot,
                ApsSnapshotId = id, Device = "pump",
            });
            glucose.Add(new SensorGlucose { Timestamp = at, Mgdl = 90 + step % 120 });
            if (step % 3 == 0)
                doses.Add(new InsulinDose(t, 0.1, true));
        }

        var stopwatch = Stopwatch.StartNew();

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(
            clipped.Select(tb => new BasalPoint { Timestamp = tb.StartMills, Rate = tb.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm }),
            to, evidence);
        var scheduled = new ScheduledBasal([new BasalSegment(compareFrom, to, 0.8, 0.8, "Default")], []).Segments(clipped);
        var inputs = new LoopHealthInputs
        {
            Zone = Zone,
            Cycles = cycles,
            Insulin = new NetInsulinAboveSchedule(delivered, scheduled, doses),
            Evidence = evidence,
            Temps = [.. clipped.OrderBy(t => t.StartMills)],
            Glucose = glucose,
        };
        var filter = new LoopAnalysisMasks();
        var primary = LoopHealthCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, from, to, Zone, new LoopAnalysisMaskSources()), from, to);
        var comparison = LoopHealthCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, compareFrom, from, Zone, new LoopAnalysisMaskSources()), compareFrom, from);

        stopwatch.Stop();
        output.WriteLine($"180 days ({cycles.Count} cycles, {temps.Count} temps, {doses.Count} SMBs): {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new LoopHealthResponse { Primary = primary, Comparison = comparison },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        output.WriteLine($"payload: {payload.Length} bytes");

        Assert.Equal(90, primary.Days.Count);
        Assert.NotNull(primary.MaxBasal!.AtCap.Percent);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}, budget {Budget}");
    }
}
