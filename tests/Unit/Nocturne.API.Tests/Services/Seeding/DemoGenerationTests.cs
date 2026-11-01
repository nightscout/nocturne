using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;
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
    public void CoverageSeeds_PinTheCompressionArtifactAndSilenceTheComparisonSensorThroughIt()
    {
        var night = new DateOnly(2026, 3, 17);
        var from = new DateTime(2026, 3, 18, 3, 35, 0, DateTimeKind.Local);
        var steps = Enumerable.Range(0, 13)
            .Select(index =>
            {
                var time = from.AddMinutes(index * 5);
                return new DemoTimeStep
                {
                    Time = time,
                    Utc = time.ToUniversalTime(),
                    Entry = new Entry
                    {
                        Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
                        Date = time,
                        Mgdl = 120,
                        Sgv = 120,
                    },
                    Iob = 0,
                    Cob = 0,
                    EffectiveIsf = 50,
                    EffectiveCarbRatio = 10,
                    Scenario = DayScenario.Normal,
                };
            })
            .ToList();

        var readings = steps.SelectMany(step => DemoCoverageSeeds.CgmReadings(step)).ToList();

        DemoCoverageSeeds.IsCompressionNight(night).Should().BeTrue();
        readings.Where(entry => entry.Device != DemoCoverageSeeds.ComparisonCgmSerial)
            .Select(entry => entry.Sgv)
            .Should().Equal(120, 110, 110, 110, 110, 110, 90, 58, 82, 108, 120, 120, 120);
        var comparison = readings.Should().ContainSingle(
            entry => entry.Device == DemoCoverageSeeds.ComparisonCgmSerial).Subject;
        comparison.Mills.Should().Be(new DateTimeOffset(from.AddMinutes(57)).ToUnixTimeMilliseconds());
        comparison.Sgv.Should().BeInRange(118, 122);

        var nadir = DemoCoverageSeeds.CompressionNadirMills(night, DemoTestClock.Host);
        var detector = new CompressionLowDetectionService(TimeProvider.System, NullLogger<CompressionLowDetectionService>.Instance);
        detector.DetectVShapeCandidates(readings.OrderBy(entry => entry.Mills).ToList())
            .Should().ContainSingle(candidate => candidate.LowestGlucose == DemoCoverageSeeds.CompressionNadirMgdl
                && candidate.RecoveryMinutes == 10
                && candidate.StartMills <= nadir
                && candidate.EndMills >= nadir);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(31)]
    [InlineData(47)]
    [InlineData(50)]
    [InlineData(69)]
    public void CoverageSeeds_CompressionArtifactIsFoundWhateverTheOvernightTrace(int seed)
    {
        var end = new DateTime(2026, 10, 20, 4, 4, 0);
        var generator = new DemoDataGenerator(
            Options.Create(new DemoModeConfiguration { BackfillDays = 14, RandomSeed = seed }),
            DemoTestClock.Host,
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance);
        var readings = generator.GenerateHistoricalTimeline(end)
            .SelectMany(step => DemoCoverageSeeds.CgmReadings(step))
            .OrderBy(entry => entry.Mills)
            .ToList();
        var detector = new CompressionLowDetectionService(TimeProvider.System, NullLogger<CompressionLowDetectionService>.Instance);
        var nights = Enumerable.Range(1, 10)
            .Select(daysAgo => DateOnly.FromDateTime(end).AddDays(-daysAgo))
            .Where(DemoCoverageSeeds.IsCompressionNight)
            .ToList();

        nights.Should().HaveCount(4);
        foreach (var night in nights)
        {
            var (windowStart, windowEnd) = TimeZoneHelper.GetOvernightWindow(night, TimeZoneInfo.Local);
            var nadir = DemoCoverageSeeds.CompressionNadirMills(night, DemoTestClock.Host);
            var overnight = readings
                .Where(entry => entry.Mills >= windowStart && entry.Mills <= windowEnd)
                .ToList();

            detector.DetectVShapeCandidates(overnight).Should().Contain(
                candidate => candidate.StartMills <= nadir
                    && candidate.EndMills >= nadir
                    && candidate.LowestGlucose <= DemoCoverageSeeds.CompressionNadirMgdl,
                $"the night of {night} carries the planted compression low");
        }
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
        var now = new DateTime(2026, 3, 17, 23, 0, 0);
        var schedule = DemoDeviceLifecycle.GenerateSchedule(now, 30, DemoTestClock.Host);

        schedule.Should().BeEquivalentTo(
            DemoDeviceLifecycle.GenerateSchedule(now, 30, DemoTestClock.Host),
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
        var (heartRates, steps) = DemoHealthDataGenerator.GenerateDailyActivity(day, "dev-sample", DemoTestClock.Host);

        var (heartRates2, steps2) = DemoHealthDataGenerator.GenerateDailyActivity(day, "dev-sample", DemoTestClock.Host);
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
            .GenerateDailyActivity(exerciseDay, "dev-sample", DemoTestClock.Host).StepCounts.Sum(s => s.Metric);
        var sickSteps = DemoHealthDataGenerator
            .GenerateDailyActivity(sickDay, "dev-sample", DemoTestClock.Host).StepCounts.Sum(s => s.Metric);

        exerciseSteps.Should().BeGreaterThan(sickSteps * 3);
    }

    [Fact]
    public void HealthGenerator_SleepSessionIsInternallyConsistent()
    {
        var morning = new DateTime(2026, 3, 17);
        var session = DemoHealthDataGenerator.GenerateSleepSession(morning, "dev-sample", DemoTestClock.Host);

        session.EndTime.Should().BeAfter(session.StartTime);
        session.TotalSleepMs.Should().Be(
            session.DeepSleepMs + session.LightSleepMs + session.RemSleepMs);
        (session.TotalSleepMs + session.TotalAwakeMs).Should().Be(session.DurationMs);
        session.Efficiency.Should().BeInRange(0, 100);
        session.Stages.Should().BeInAscendingOrder(s => s.StartTime);
        session.OriginalId.Should().Be($"dev-sample:sleep:{session.StartTime:yyyy-MM-dd}");

        // Deterministic per night.
        DemoHealthDataGenerator.GenerateSleepSession(morning, "dev-sample", DemoTestClock.Host)
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
            DemoHealthDataGenerator.GenerateSleepSession(d, "dev-sample", DemoTestClock.Host).Efficiency!.Value);
        var normalAvg = normalNights.Average(d =>
            DemoHealthDataGenerator.GenerateSleepSession(d, "dev-sample", DemoTestClock.Host).Efficiency!.Value);

        poorAvg.Should().BeLessThan(normalAvg);
    }
}
