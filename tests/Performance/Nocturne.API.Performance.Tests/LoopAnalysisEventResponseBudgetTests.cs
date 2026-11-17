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
/// The Meals &amp; events compute over the widest range it accepts, 90 days of five-minute AID data
/// with three meals and an unannounced rise a day, for three cohorts over the longest window and
/// a comparison range of the same size, IOB/COB recomputed at every tick.
/// </summary>
public class LoopAnalysisEventResponseBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const long Minute = 60_000;
    private const long Day = 24 * 60 * Minute;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    [Trait("Category", "Performance")]
    public void NinetyDaysOfMealsAndRises_ComputeWithinBudget()
    {
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();
        var from = to - 90 * Day;
        var compareFrom = from - 90 * Day;
        var end = to + Day;

        var temps = new List<TempBasal>();
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var glucose = new List<GlucoseSample>();
        for (var t = compareFrom - Day; t < end; t += 5 * Minute)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            var minuteOfDay = (t - compareFrom) % Day / Minute;
            temps.Add(new TempBasal
            {
                StartTimestamp = at, EndTimestamp = at.AddMinutes(30), Rate = 0.4 + (t / (5 * Minute) % 7) * 0.2,
                Origin = TempBasalOrigin.Algorithm, Device = "pump",
            });
            var sinceMeal = minuteOfDay % 360;
            glucose.Add(new GlucoseSample(t, sinceMeal < 60 ? 110 + sinceMeal * 1.5 : Math.Max(110, 200 - (sinceMeal - 60) * 0.6)));
            if (minuteOfDay % 360 == 0)
            {
                boluses.Add(new Bolus { Timestamp = at, Insulin = 4 });
                if (minuteOfDay != 0)
                    carbs.Add(new CarbIntake { Timestamp = at, Carbs = 45 });
            }
            if (t % (15 * Minute) == 0)
                boluses.Add(new Bolus { Timestamp = at, Insulin = 0.1, Kind = BolusKind.Algorithm });
        }

        var snapshot = new TherapySnapshot(
            dia: 5, carbsPerHour: 30, timezone: Zone, ccpPercentage: null, ccpTimeshiftMs: 0,
            sensitivityEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 50 }],
            carbRatioEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 10 }],
            basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 0.8 }]);
        var timeline = new TherapyTimeline([new TherapySegment(compareFrom - Day, end + 1, snapshot)]);
        var iob = new IobCalculator(null!, null!, null!, null!, null!, null!);
        var cob = new CobCalculator(NullLogger<CobCalculator>.Instance, iob, null!, null!, null!, null!);
        var definition = new EventResponseDefinition
        {
            MinutesAfter = EventResponseDefinition.MinutesAfterChoices[^1],
            Cohorts =
            [
                new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FromMinute = 5 * 60, ToMinute = 10 * 60 },
                new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, MinGrams = 30 },
                new EventCohortDefinition { Cohort = 3, Anchor = EventAnchorKind.UnannouncedRise },
            ],
        };

        var stopwatch = Stopwatch.StartNew();

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var series = clipped.Select(tb => new BasalPoint
        {
            Timestamp = tb.StartMills, Rate = tb.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm,
        });
        var start = compareFrom - Day;
        var inputs = new EventResponseInputs
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
            },
        };
        var masks = new LoopAnalysisMasks();
        var primary = EventResponseCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(masks, from, to, Zone, inputs.Sources), from, to, definition);
        var comparison = EventResponseCalculator.Calculate(
            inputs, LoopAnalysisMask.Build(masks, compareFrom, from, Zone, inputs.Sources), compareFrom, from, definition,
            listEvents: false);

        stopwatch.Stop();
        output.WriteLine($"180 days ({glucose.Count} readings, {primary.Events.Count} listed events): {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new EventResponseReport { Primary = primary, Comparison = comparison },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        output.WriteLine($"payload: {payload.Length} bytes");

        Assert.True(primary.Cohorts[1].Events > 0);
        Assert.True(primary.Cohorts[2].Events > 0, "the unannounced midnight rise is found every day");
        Assert.Equal(108, primary.Cohorts[0].Slots.Count);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}, budget {Budget}");
    }
}
