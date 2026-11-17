using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The Daily profile's compute over the widest range it accepts, 90 days of five-minute AID data,
/// with IOB/COB recomputed at every tick (no APS snapshots: the costliest path) and a comparison
/// range of the same size.
/// </summary>
public class LoopAnalysisDailyProfileBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const long Minute = 60_000;
    private const long Day = 24 * 60 * Minute;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    [Trait("Category", "Performance")]
    public void NinetyDaysOfFiveMinuteData_ComputesWithinBudget()
    {
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();
        var from = to - 90 * Day;
        var compareFrom = from - 90 * Day;

        var temps = new List<TempBasal>();
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var glucose = new List<GlucoseSample>();
        for (var t = compareFrom; t < to; t += 5 * Minute)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            temps.Add(new TempBasal
            {
                StartTimestamp = at, EndTimestamp = at.AddMinutes(30), Rate = 0.4 + (t / (5 * Minute) % 7) * 0.2,
                Origin = TempBasalOrigin.Algorithm, Device = "pump",
            });
            glucose.Add(new GlucoseSample(t, 90 + t / (5 * Minute) % 120));
            if (t % (15 * Minute) == 0)
                boluses.Add(new Bolus { Timestamp = at, Insulin = 0.1, Kind = BolusKind.Algorithm });
            if (t % (8 * 60 * Minute) == 0)
            {
                boluses.Add(new Bolus { Timestamp = at, Insulin = 4 });
                carbs.Add(new CarbIntake { Timestamp = at, Carbs = 45 });
            }
        }

        var snapshot = new TherapySnapshot(
            dia: 5, carbsPerHour: 30, timezone: Zone, ccpPercentage: null, ccpTimeshiftMs: 0,
            sensitivityEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 50 }],
            carbRatioEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 10 }],
            basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 0.8 }]);
        var timeline = new TherapyTimeline([new TherapySegment(compareFrom, to + 1, snapshot)]);
        var iob = new IobCalculator(null!, null!, null!, null!, null!, null!);
        var cob = new CobCalculator(NullLogger<CobCalculator>.Instance, iob, null!, null!, null!, null!);
        var overrides = Enumerable.Range(0, 26)
            .Select(w => new ScaleFactorInterval(compareFrom + w * 7 * Day, compareFrom + w * 7 * Day + 3 * 60 * Minute, 0.8))
            .ToList();
        var filter = new LoopAnalysisMasks { ExcludeOverrides = true };

        var stopwatch = Stopwatch.StartNew();

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var series = clipped.Select(tb => new BasalPoint
        {
            Timestamp = tb.StartMills, Rate = tb.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm,
        });
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(series, to, evidence);
        var basal = new ScheduledBasal([new BasalSegment(compareFrom, to, 0.8, 0.8, "Default")], overrides);
        var scheduled = basal.Segments(clipped);
        var ticks = IobCobSeries.Build(iob, cob, boluses, carbs, temps, timeline, basal, null, compareFrom, to - 1, to - 1, 5 * Minute);

        var inputs = new DailyProfileInputs
        {
            Zone = Zone,
            Glucose = glucose,
            Insulin = new NetInsulinAboveSchedule(
                delivered, scheduled, boluses.Select(b => new InsulinDose(b.Mills, b.Insulin, b.Kind == BolusKind.Algorithm))),
            Carbs = [.. carbs.Select(c => new CarbEvent(c.Mills, c.Carbs))],
            IobCob = ticks,
            CalculatedIobCobTime = DailyProfileCalculator.CalculableIobCobTime(
                evidence, boluses.Select(b => b.Mills).Concat(carbs.Select(c => c.Mills))),
            TargetAt = _ => (90, 140),
        };
        var sources = new LoopAnalysisMaskSources { Overrides = [.. overrides.Select(o => new MillsInterval(o.Start, o.End))] };
        var primary = DailyProfileCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, from, to, Zone, sources), from, to);
        var comparison = DailyProfileCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(filter, compareFrom, from, Zone, sources), compareFrom, from);

        stopwatch.Stop();
        output.WriteLine($"180 days ({temps.Count} temps, {ticks.Count} IOB/COB ticks): {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DailyProfileResponse { Primary = primary, Comparison = comparison },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        output.WriteLine($"payload: {payload.Length} bytes");

        Assert.Equal(90, primary.Days.Count);
        Assert.Equal(90, comparison.Days.Count);
        Assert.NotNull(primary.Slots[100].NetLoopCorrection);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}, budget {Budget}");
    }
}
