using FluentAssertions;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// Tests for the scenario-correlated demo generators backing
/// <see cref="Nocturne.API.Services.Seeding.SampleDataSeeder"/>.
/// </summary>
public class DemoGenerationTests
{
    [Fact]
    public void DayScenarios_AreDeterministicPerDate()
    {
        var date = new DateTime(2026, 3, 17);
        var first = DayScenarios.For(date);

        for (var i = 0; i < 10; i++)
            DayScenarios.For(date).Should().Be(first);
    }

    [Fact]
    public void DayScenarios_NormalIsMostCommonOverAYear()
    {
        var start = new DateTime(2026, 1, 1);
        var counts = Enumerable.Range(0, 365)
            .Select(d => DayScenarios.For(start.AddDays(d)))
            .GroupBy(s => s)
            .ToDictionary(g => g.Key, g => g.Count());

        counts[DayScenario.Normal].Should().BeGreaterThan(
            counts.Where(kv => kv.Key != DayScenario.Normal).Max(kv => kv.Value));
    }

    [Fact]
    public void EpisodeTracker_EmitsEpisodeForSustainedLow()
    {
        var tracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);
        var t = new DateTime(2026, 3, 17, 2, 0, 0, DateTimeKind.Utc);

        // 30 minutes in range, 30 minutes below 70, back in range.
        for (var i = 0; i < 6; i++) tracker.Observe(t.AddMinutes(i * 5), 110);
        for (var i = 6; i < 12; i++) tracker.Observe(t.AddMinutes(i * 5), 62);
        for (var i = 12; i < 15; i++) tracker.Observe(t.AddMinutes(i * 5), 95);
        tracker.Flush();

        tracker.Episodes.Should().ContainSingle(e => e.RuleName == "Low");
        var episode = tracker.Episodes.Single(e => e.RuleName == "Low");
        (episode.EndUtc - episode.StartUtc).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void EpisodeTracker_IgnoresBriefDips()
    {
        var tracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);
        var t = new DateTime(2026, 3, 17, 2, 0, 0, DateTimeKind.Utc);

        tracker.Observe(t, 110);
        tracker.Observe(t.AddMinutes(5), 65); // one stray reading
        tracker.Observe(t.AddMinutes(10), 100);
        tracker.Flush();

        tracker.Episodes.Should().BeEmpty();
    }

    [Fact]
    public void EpisodeTracker_FlushClosesOpenEpisode()
    {
        var tracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);
        var t = new DateTime(2026, 3, 17, 2, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 6; i++) tracker.Observe(t.AddMinutes(i * 5), 280);
        tracker.Flush();

        tracker.Episodes.Should().ContainSingle(e => e.RuleName == "High");
    }

    [Fact]
    public void DeviceLifecycle_EveryTriggerEventTypeDecomposesToADeviceEvent()
    {
        // The dashboard age pills only work if the decomposer recognizes the
        // seeded treatment EventTypes.
        foreach (var spec in DemoDeviceLifecycle.TrackerSpecs)
            TreatmentTypes.DeviceEventTypeMap.Should().ContainKey(spec.TriggerEventType);
    }

    [Fact]
    public void DeviceLifecycle_ScheduleIsDeterministicChronologicalAndCoversAllKinds()
    {
        var today = new DateTime(2026, 3, 17);
        var schedule = DemoDeviceLifecycle.GenerateSchedule(today, 30);

        schedule.Should().BeEquivalentTo(
            DemoDeviceLifecycle.GenerateSchedule(today, 30),
            o => o.WithStrictOrdering());
        schedule.Should().BeInAscendingOrder(e => e.TimestampUtc);
        foreach (var spec in DemoDeviceLifecycle.TrackerSpecs)
        {
            schedule.Should().Contain(e => e.EventType == spec.TriggerEventType,
                $"a 30-day window must contain at least one {spec.Name} change");
        }
    }

    [Fact]
    public void HealthGenerator_ActivityIsDeterministicAndPlausible()
    {
        var day = new DateTime(2026, 3, 17);
        var (heartRates, steps) = DemoHealthDataGenerator.GenerateDailyActivity(day, "dev-sample");

        var (heartRates2, steps2) = DemoHealthDataGenerator.GenerateDailyActivity(day, "dev-sample");
        heartRates.Select(h => (h.Timestamp, h.Bpm))
            .Should().BeEquivalentTo(heartRates2.Select(h => (h.Timestamp, h.Bpm)));
        steps.Select(s => (s.Timestamp, s.Metric))
            .Should().BeEquivalentTo(steps2.Select(s => (s.Timestamp, s.Metric)));

        heartRates.Should().HaveCount(288); // 5-minute cadence
        heartRates.Should().OnlyContain(h => h.Bpm >= 40 && h.Bpm <= 200);
        heartRates.Should().OnlyContain(h => h.SyncIdentifier!.StartsWith("dev-sample:hr:"));
        steps.Should().OnlyContain(s => s.Metric > 0 && s.Metric < 10_000);
        steps.Sum(s => s.Metric).Should().BeInRange(1_000, 40_000);
    }

    [Fact]
    public void HealthGenerator_ExerciseDayOutStepsSickDay()
    {
        // Scan a fixed year for one date of each scenario, then compare totals.
        var start = new DateTime(2026, 1, 1);
        var dates = Enumerable.Range(0, 365).Select(d => start.AddDays(d)).ToList();
        var exerciseDay = dates.First(d => DayScenarios.For(d) == DayScenario.Exercise);
        var sickDay = dates.First(d => DayScenarios.For(d) == DayScenario.SickDay);

        var exerciseSteps = DemoHealthDataGenerator
            .GenerateDailyActivity(exerciseDay, "dev-sample").StepCounts.Sum(s => s.Metric);
        var sickSteps = DemoHealthDataGenerator
            .GenerateDailyActivity(sickDay, "dev-sample").StepCounts.Sum(s => s.Metric);

        exerciseSteps.Should().BeGreaterThan(sickSteps * 3);
    }

    [Fact]
    public void HealthGenerator_SleepSessionIsInternallyConsistent()
    {
        var morning = new DateTime(2026, 3, 17);
        var session = DemoHealthDataGenerator.GenerateSleepSession(morning, "dev-sample");

        session.EndTime.Should().BeAfter(session.StartTime);
        session.TotalSleepMs.Should().Be(
            session.DeepSleepMs + session.LightSleepMs + session.RemSleepMs);
        (session.TotalSleepMs + session.TotalAwakeMs).Should().Be(session.DurationMs);
        session.Efficiency.Should().BeInRange(0, 100);
        session.Stages.Should().BeInAscendingOrder(s => s.StartTime);
        session.OriginalId.Should().Be($"dev-sample:sleep:{session.StartTime:yyyy-MM-dd}");

        // Deterministic per night.
        DemoHealthDataGenerator.GenerateSleepSession(morning, "dev-sample")
            .DurationMs.Should().Be(session.DurationMs);
    }

    [Fact]
    public void HealthGenerator_PoorSleepNightIsWorseThanNormalNight()
    {
        var start = new DateTime(2026, 1, 1);
        var dates = Enumerable.Range(0, 365).Select(d => start.AddDays(d)).ToList();
        var poorNights = dates.Where(d => DayScenarios.For(d) == DayScenario.PoorSleep).Take(5);
        var normalNights = dates.Where(d => DayScenarios.For(d) == DayScenario.Normal).Take(5);

        var poorAvg = poorNights.Average(d =>
            DemoHealthDataGenerator.GenerateSleepSession(d, "dev-sample").Efficiency!.Value);
        var normalAvg = normalNights.Average(d =>
            DemoHealthDataGenerator.GenerateSleepSession(d, "dev-sample").Efficiency!.Value);

        poorAvg.Should().BeLessThan(normalAvg);
    }
}
