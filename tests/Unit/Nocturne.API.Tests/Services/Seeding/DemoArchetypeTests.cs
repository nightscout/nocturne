using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Demo;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// What each person of the demo clinic must show whenever someone looks: the seeded history up to the
/// instant of the seed, then the timeline the live feed carries on from it, read at every five minutes
/// of a run that crosses a local midnight.
/// </summary>
public class DemoArchetypeTests
{
    private const int Days = DemoClinic.PersonBackfillDays;

    private static readonly DemoClock Sydney = new(
        TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));

    /// <summary>
    /// Seed ends a few weeks apart, each 19:00 or 18:00 in Sydney, so the 36 hours after each cross a local midnight
    /// and the 14-day windows hold different days.
    /// </summary>
    private static readonly DateTime[] SeedEnds =
    [
        new(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc),
        new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc),
        new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc),
        new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
    ];

    private static readonly TimeSpan RunLength = TimeSpan.FromHours(36);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, DateTime), Lazy<Run>> Runs = new();

    private sealed record Look(DateTime Now, Entry? Latest, IReadOnlyList<Entry> Readings);

    private sealed record Run(DateTime SeedEnd, IReadOnlyList<DemoTimeStep> Steps, IReadOnlyList<Entry> Readings)
    {
        public IEnumerable<Look> Looks()
        {
            for (var now = SeedEnd; now <= SeedEnd + RunLength; now = now.AddMinutes(5))
            {
                var mills = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                var visible = Readings.Where(r => r.Mills <= mills).ToList();
                yield return new Look(now, visible.MaxBy(r => r.Mills), visible);
            }
        }
    }

    private static Run Simulate(DemoArchetype archetype, DateTime seedEnd)
    {
        var config = archetype.ConfigurationFor(Days);
        DemoDataGenerator Generator() => new(
            Options.Create(config), Sydney, NullLogger<DemoDataGenerator>.Instance, NullLoggerFactory.Instance);

        var seeded = Generator().GenerateHistoricalTimeline(seedEnd).ToList();
        var firstDay = Sydney.ToLocal(seedEnd).AddDays(-Days).Date;
        var live = Generator().ContinueTimeline(firstDay, seedEnd)
            .SkipWhile(step => step.Utc < seedEnd)
            .TakeWhile(step => step.Utc <= seedEnd + RunLength)
            .ToList();

        IReadOnlyList<DemoTimeStep> steps = [.. seeded, .. live];
        return new Run(seedEnd, steps, [.. steps.SelectMany(step => DemoCoverageSeeds.PrimaryReadings(step))]);
    }

    private static IEnumerable<Run> RunsOf(string key) => SeedEnds.Select(end =>
        Runs.GetOrAdd((key, end), _ => new Lazy<Run>(() => Simulate(DemoArchetypes.Find(key)!, end))).Value);

    private static double TimeInRange(Look look, TimeSpan window)
    {
        var from = new DateTimeOffset(look.Now - window).ToUnixTimeMilliseconds();
        var readings = look.Readings.Where(r => r.Mills > from).ToList();
        return 100.0 * readings.Count(r => r.Sgv is >= 70 and <= 180) / readings.Count;
    }

    private static readonly string[] SteadyKeys = ["john", "paula", "liam", "maddie", "sam", "gus"];

    private static readonly string[] InRangeKeys = [.. SteadyKeys, "katie", "lena"];

    [Fact]
    public void Every_person_of_the_clinic_has_one_row_and_no_row_is_for_anyone_else()
    {
        DemoArchetypes.All.Select(a => a.Key).Should().BeEquivalentTo(DemoClinic.People.Select(p => p.Key));
        DemoArchetypes.All.Select(a => a.Seed).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Each_run_crosses_a_local_midnight()
    {
        foreach (var seedEnd in SeedEnds)
            Sydney.ToLocal(seedEnd + RunLength).Date.Should().Be(Sydney.ToLocal(seedEnd).Date.AddDays(2));
    }

    [Fact]
    public void Katie_Staledata_has_no_reading_from_the_last_two_hours_and_no_fingerstick_or_calibration_while_quiet()
    {
        foreach (var run in RunsOf("katie"))
        {
            var looks = run.Looks().ToList();

            looks.Should().OnlyContain(look => look.Latest != null);
            looks.Should().OnlyContain(look => look.Now - look.Latest!.Mills.ToUtcDateTime() > TimeSpan.FromHours(2));
            run.Steps.Where(step => step.Loop == DemoLoopState.SignalLost).Should().NotBeEmpty()
                .And.OnlyContain(step => step.ExtraEntries.Count == 0);
        }
    }

    [Fact]
    public void Lena_Lowlastnight_has_a_low_in_the_last_24_hours_at_any_look_and_it_is_in_the_night()
    {
        foreach (var run in RunsOf("lena"))
        {
            foreach (var look in run.Looks())
            {
                var from = new DateTimeOffset(look.Now.AddHours(-24)).ToUnixTimeMilliseconds();
                look.Readings.Where(r => r.Mills > from).Should()
                    .Contain(r => r.Sgv < 70, $"a visitor at {look.Now:u} looks at the last 24 hours");
            }

            run.Readings.Where(r => r.Sgv < 70).Select(r => Sydney.ToLocal(r.Mills.ToUtcDateTime()).Hour)
                .Should().OnlyContain(hour => hour == 3, "her low is at 03:00 local");
        }
    }

    [Fact]
    public void Tom_Belowtarget_is_under_the_time_in_range_target_over_14_days_at_every_look()
    {
        foreach (var run in RunsOf("tom"))
        {
            foreach (var look in run.Looks().Where(l => l.Now.Minute == 0))
                TimeInRange(look, TimeSpan.FromDays(14)).Should().BeLessThan(70, $"at {look.Now:u}");
        }
    }

    [Fact]
    public void Rory_Risingfast_reads_rising_at_nearly_every_look_and_is_under_the_target()
    {
        foreach (var run in RunsOf("rory"))
        {
            var looks = run.Looks().ToList();

            var rising = looks.Count(look => look.Latest!.Direction is "FortyFiveUp" or "SingleUp" or "DoubleUp");
            ((double)rising / looks.Count).Should().BeGreaterThanOrEqualTo(0.98);
            looks.Where(look => look.Latest!.Direction is not ("FortyFiveUp" or "SingleUp" or "DoubleUp"))
                .Select(look => Sydney.ToLocal(look.Now).TimeOfDay.TotalMinutes % DemoArchetype.RiseCycle.TotalMinutes)
                .Should().OnlyContain(minute => minute < 5);
            looks.Where(l => l.Now.Minute == 0).Should().OnlyContain(look => TimeInRange(look, TimeSpan.FromDays(14)) < 70);
        }
    }

    [Fact]
    public void The_people_who_keep_in_range_stay_over_the_time_in_range_target()
    {
        foreach (var key in InRangeKeys)
        {
            foreach (var run in RunsOf(key))
            {
                foreach (var look in run.Looks().Where(l => l.Now.Minute == 0))
                    TimeInRange(look, TimeSpan.FromDays(14)).Should().BeGreaterThanOrEqualTo(70, $"{key} at {look.Now:u}");
            }
        }
    }

    [Fact]
    public void The_steady_people_have_no_low_in_the_last_24_hours_at_any_look()
    {
        foreach (var key in SteadyKeys)
        {
            foreach (var run in RunsOf(key))
            {
                foreach (var look in run.Looks().Where(l => l.Now.Minute == 0))
                {
                    var from = new DateTimeOffset(look.Now.AddHours(-24)).ToUnixTimeMilliseconds();
                    look.Readings.Where(r => r.Mills > from).Should().NotContain(r => r.Sgv < 70, $"{key} at {look.Now:u}");
                }
            }
        }
    }

    [Fact]
    public void Only_a_looping_person_uploads_a_loop_status_and_only_a_pump_person_sets_temp_basals_by_hand()
    {
        foreach (var archetype in DemoArchetypes.All)
        {
            var steps = RunsOf(archetype.Key).First().Steps;
            var tempBasals = steps.SelectMany(s => s.Treatments).Count(t => t.EventType == "Temp Basal");

            (steps.Any(s => s.Loop is DemoLoopState.Closed)).Should().Be(archetype.Therapy == DemoTherapy.Loop, archetype.Key);
            (tempBasals > 0).Should().Be(archetype.Therapy != DemoTherapy.Mdi, archetype.Key);
        }
    }

    [Fact]
    public void Two_steady_people_have_different_days_nights_and_activity_over_a_week()
    {
        var john = DemoArchetypes.Find("john")!;
        var sam = DemoArchetypes.Find("sam")!;
        var days = Enumerable.Range(0, 28).Select(d => new DateTime(2026, 10, 1).AddDays(d)).ToList();

        days.Select(day => DayScenarios.For(day, john)).Should().NotEqual(days.Select(day => DayScenarios.For(day, sam)));

        var week = days.Take(7).ToList();
        string Night(DateTime day, DemoArchetype who)
        {
            var sleep = DemoHealthDataGenerator.GenerateSleepSession(day, "demo-service", Sydney, who);
            return $"{sleep.StartTime:o}|{sleep.DeepSleepMs}|{sleep.RemSleepMs}";
        }
        string Activity(DateTime day, DemoArchetype who) => string.Join(
            ',', DemoHealthDataGenerator.GenerateDailyActivity(day, "demo-service", Sydney, who).HeartRates.Select(h => h.Bpm));

        week.Select(day => Night(day, john)).Should().NotEqual(week.Select(day => Night(day, sam)));
        week.Count(day => Night(day, john) != Night(day, sam)).Should().BeGreaterThanOrEqualTo(5);
        week.Count(day => Activity(day, john) != Activity(day, sam)).Should().BeGreaterThanOrEqualTo(5);
        var changeDays = days.Where(day => DemoDeviceLifecycle.ChangeTimeOn(day, TreatmentTypes.SensorStart) is not null).ToList();
        changeDays.Should().NotBeEmpty();
        changeDays.Select(day => DemoDeviceLifecycle.ChangeTimeOn(day, TreatmentTypes.SensorStart, john))
            .Should().NotEqual(changeDays.Select(day => DemoDeviceLifecycle.ChangeTimeOn(day, TreatmentTypes.SensorStart, sam)));
    }
}
