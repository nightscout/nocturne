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
/// The Settings evidence compute over the widest range it accepts, 90 days of five-minute AID data
/// with three meals, a correction and an unannounced rise a day, and a comparison range of the same
/// size, IOB/COB recomputed at every tick.
/// </summary>
public class LoopAnalysisSettingsEvidenceBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Day = 24 * Hour;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    [Trait("Category", "Performance")]
    public void NinetyDaysWithAComparison_ComputeWithinBudget()
    {
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();
        var from = to - 90 * Day;
        var compareFrom = from - 90 * Day;
        var start = compareFrom - Day;
        var end = to + Day;

        var temps = new List<TempBasal>();
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var glucose = new List<GlucoseSample>();
        for (var t = start; t < end; t += 5 * Minute)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            var minuteOfDay = (t - start) % Day / Minute;
            temps.Add(new TempBasal
            {
                StartTimestamp = at, EndTimestamp = at.AddMinutes(30), Rate = 0.4 + (t / (5 * Minute) % 7) * 0.2,
                Origin = TempBasalOrigin.Algorithm, Device = "pump",
            });
            glucose.Add(new GlucoseSample(t, minuteOfDay switch
            {
                >= 420 and < 480 => 110 + (minuteOfDay - 420) * 1.5,
                >= 480 and < 660 => 200 - (minuteOfDay - 480) * 0.5,
                >= 900 and < 960 => 110 + (minuteOfDay - 900) * 2,
                >= 960 and < 1200 => 230 - (minuteOfDay - 960) * 0.5,
                _ => 110,
            }));
            if (minuteOfDay is 420 or 720 or 1140)
            {
                carbs.Add(new CarbIntake { Timestamp = at, Carbs = 45 });
                boluses.Add(new Bolus { Timestamp = at, Insulin = 4.5 });
            }
            if (minuteOfDay == 960)
                boluses.Add(new Bolus { Timestamp = at, Insulin = 2 });
            if (t % (15 * Minute) == 0)
                boluses.Add(new Bolus { Timestamp = at, Insulin = 0.1, Kind = BolusKind.Algorithm });
        }

        var snapshot = new TherapySnapshot(
            dia: 5, peakMinutes: 75, carbsPerHour: 30, timezone: Zone, ccpPercentage: null, ccpTimeshiftMs: 0,
            sensitivityEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 50 }],
            carbRatioEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 10 }],
            basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 0.8 }]);
        var timeline = new TherapyTimeline([new TherapySegment(start, end + 1, snapshot)]);
        var iob = new IobCalculator(null!, null!, null!, null!, null!, null!);
        var cob = new CobCalculator(NullLogger<CobCalculator>.Instance, iob, null!, null!, null!, null!);

        var stopwatch = Stopwatch.StartNew();

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var series = clipped.Select(tb => new BasalPoint
        {
            Timestamp = tb.StartMills, Rate = tb.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm,
        });
        var inputs = new SettingsEvidenceInputs
        {
            Series = new DailyProfileInputs
            {
                Zone = Zone,
                Glucose = glucose,
                Insulin = new NetInsulinAboveSchedule(
                    NetInsulinAboveSchedule.DeliveredFromSeries(series, end, evidence),
                    new ScheduledBasal([new BasalSegment(start, end, 0.8, 0.8, "Default")], []).Segments(clipped),
                    boluses.Select(b => new InsulinDose(b.Mills, b.Insulin, b.Kind == BolusKind.Algorithm))),
                Carbs = [.. carbs.Select(c => new CarbEvent(c.Mills, c.Carbs))],
                IobCob = IobCobSeries.Build(iob, cob, boluses, carbs, temps, timeline, new ScheduledBasal(timeline.BasalSegments(), []), null, start, end - 1, end - 1, 5 * Minute),
                CalculatedIobCobTime = DailyProfileCalculator.CalculableIobCobTime(
                    evidence, boluses.Select(b => b.Mills).Concat(carbs.Select(c => c.Mills))),
                TargetAt = _ => (90, 140),
            },
            Therapy = timeline,
        };
        var masks = new LoopAnalysisMasks();
        var primary = SettingsEvidenceCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(masks, from, to, Zone, inputs.Sources), from, to);
        var comparison = SettingsEvidenceCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(masks, compareFrom, from, Zone, inputs.Sources), compareFrom, from, listEpisodes: false);

        stopwatch.Stop();
        output.WriteLine($"180 days ({glucose.Count} readings): {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new SettingsEvidenceReport { Primary = primary, Comparison = comparison },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        output.WriteLine($"payload: {payload.Length} bytes");

        Assert.Equal(12, primary.Basal.Count);
        Assert.True(primary.Basal.Sum(b => b.N) > 0, "the night is fasting");
        Assert.True(primary.CarbRatio.Sum(b => b.N) + primary.CarbRatio.Sum(b => b.Rejections.Sum(r => r.Count)) > 0);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}, budget {Budget}");
    }
}
