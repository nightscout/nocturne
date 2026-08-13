using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// Tests for the unified demo timeline and the streams derived from it —
/// device status, fingersticks, calibrations, notes, therapy profile, and
/// lifestyle seeds.
/// </summary>
public class DemoTimelineTests
{
    private static DemoDataGenerator CreateGenerator(int days = 5) => new(
        Options.Create(new DemoModeConfiguration { BackfillDays = days }),
        NullLogger<DemoDataGenerator>.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void Timeline_StepsAreChronologicalFiveMinuteAndNeverFuture()
    {
        var steps = CreateGenerator(3).GenerateHistoricalTimeline().ToList();

        steps.Should().NotBeEmpty();
        steps.Should().BeInAscendingOrder(s => s.Time);
        steps.Zip(steps.Skip(1))
            .Should().OnlyContain(pair =>
                pair.Second.Time - pair.First.Time == TimeSpan.FromMinutes(5)
                // Day boundaries on the last partial day may clip the step.
                || pair.Second.Time - pair.First.Time < TimeSpan.FromMinutes(5));
        steps[^1].Time.Should().BeOnOrBefore(DateTime.Now);
    }

    [Fact]
    public void Timeline_CarriesBothStreamsFromOneRun()
    {
        // One enumeration yields the chart and the treatment history together —
        // the old divergent per-stream simulation passes are gone.
        var steps = CreateGenerator(3).GenerateHistoricalTimeline().ToList();

        var entries = steps.SelectMany(s => new[] { s.Entry }.Concat(s.ExtraEntries)).ToList();
        var treatments = steps.SelectMany(s => s.Treatments).ToList();

        treatments.Should().NotBeEmpty();
        // sgv entries at 5-minute cadence plus mbg/cal extras.
        entries.Count(e => e.Type == "sgv").Should().BeGreaterThan(3 * 280);
        entries.Should().Contain(e => e.Type == "mbg", "fingersticks ride the timeline");
    }

    [Fact]
    public void Timeline_NeverEmitsScheduledBasalTreatments()
    {
        // "Scheduled Basal" was dropped: the decomposer never recognized it and
        // every regenerate logged ~2k warnings. Scheduled basal now comes from
        // the seeded therapy profile.
        var treatments = CreateGenerator(3).GenerateHistoricalTimeline()
            .SelectMany(s => s.Treatments)
            .ToList();

        treatments.Should().NotBeEmpty();
        treatments.Should().NotContain(t => t.EventType == "Scheduled Basal");
        treatments.Should().Contain(t => t.EventType == "Temp Basal");
        treatments.Should().Contain(t => t.EventType == "Carbs");
    }

    [Fact]
    public void Timeline_NothingIsFutureDated()
    {
        var nowMills = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var steps = CreateGenerator(2).GenerateHistoricalTimeline().ToList();

        var treatments = steps.SelectMany(s => s.Treatments).ToList();
        var extras = steps.SelectMany(s => s.ExtraEntries).ToList();

        treatments.Should().NotBeEmpty();
        // The final partial step clamps planned-item consumption to "now";
        // small slack covers the wall-clock advancing during enumeration.
        treatments.Should().OnlyContain(t => t.Mills <= nowMills + 60_000);
        extras.Should().OnlyContain(e => e.Mills <= nowMills + 60_000);
    }

    [Fact]
    public void Timeline_CarriesSimulatorStateForDeviceStatus()
    {
        var steps = CreateGenerator(2).GenerateHistoricalTimeline().ToList();

        steps.Should().OnlyContain(s => s.Iob >= 0 && s.Cob >= 0);
        steps.Should().Contain(s => s.Iob > 0, "boluses must show up as IOB");
        steps.Should().Contain(s => s.Cob > 0, "meals must show up as COB");
        steps.Should().OnlyContain(s => s.EffectiveIsf > 0 && s.EffectiveCarbRatio > 0);
    }

    [Fact]
    public void Timeline_IncludesNotes()
    {
        // Note rolls hit ~35% of days; over 30 days the chance of an empty
        // window is negligible (0.65^30) even though rolls are date-anchored.
        var treatments = CreateGenerator(30).GenerateHistoricalTimeline()
            .SelectMany(s => s.Treatments)
            .ToList();

        treatments.Should().Contain(t => t.EventType == "Note" || t.EventType == "Announcement");
    }

    [Fact]
    public void Timeline_CalibrationsFollowSensorStarts()
    {
        // A 21-day window always contains at least two sensor changes
        // (10-day cycle), each followed by cal entries.
        var entries = CreateGenerator(21).GenerateHistoricalTimeline()
            .SelectMany(s => s.ExtraEntries)
            .ToList();

        var calibrations = entries.Where(e => e.Type == "cal").ToList();
        calibrations.Should().NotBeEmpty();
        calibrations.Should().OnlyContain(c => c.Slope > 0 && c.Intercept > 0 && c.Scale == 1);
    }

    [Fact]
    public void DeviceStatus_IsDeterministicAndCarriesAllThreeBlocks()
    {
        var time = new DateTime(2026, 3, 17, 14, 30, 0);
        DeviceStatus Create() => DemoDeviceStatusGenerator.Create(
            time, glucose: 160, iob: 1.2, cob: 25, tempBasalRate: 1.4, tempBasalDuration: 30,
            effectiveIsf: 40, effectiveCarbRatio: 10, targetGlucose: 110,
            scheduledBasalRate: 1.0, scenario: DayScenario.Normal);

        var status = Create();

        status.Device.Should().Be("Trio");
        status.OpenAps!.Suggested!.Bg.Should().Be(160);
        status.OpenAps.Suggested.Rate.Should().Be(1.4);
        status.OpenAps.Suggested.PredBGs!.IOB.Should().HaveCountGreaterThan(30);
        status.OpenAps.Suggested.PredBGs.COB.Should().NotBeNull("carbs on board produce a COB curve");
        status.OpenAps.Enacted!.Received.Should().BeTrue();
        status.OpenAps.Iob!.Iob.Should().Be(1.2);
        status.OpenAps.Cob.Should().Be(25);
        status.Pump!.Reservoir.Should().BeInRange(8, 200);
        status.Pump.Battery!.Percent.Should().BeInRange(4, 100);
        status.Uploader!.Battery.Should().BeInRange(20, 100);

        // Deterministic: the same inputs produce the same document.
        Create().Should().BeEquivalentTo(status);
    }

    [Fact]
    public void DeviceStatus_EventualBgRespondsToIobAndCob()
    {
        DeviceStatus Create(double iob, double cob) => DemoDeviceStatusGenerator.Create(
            new DateTime(2026, 3, 17, 14, 30, 0), glucose: 160, iob: iob, cob: cob,
            tempBasalRate: null, tempBasalDuration: null,
            effectiveIsf: 40, effectiveCarbRatio: 10, targetGlucose: 110,
            scheduledBasalRate: 1.0, scenario: DayScenario.Normal);

        var withInsulin = Create(iob: 3, cob: 0).OpenAps!.Suggested!.EventualBG;
        var withCarbs = Create(iob: 0, cob: 40).OpenAps!.Suggested!.EventualBG;

        withInsulin.Should().BeLessThan(160, "insulin on board pulls eventual BG down");
        withCarbs.Should().BeGreaterThan(160, "carbs on board push eventual BG up");
    }

    [Fact]
    public void DeviceStatus_ReservoirDrainsBetweenChangesAndRefills()
    {
        // Find an Insulin Change day and compare just-before vs just-after.
        var day = Enumerable.Range(0, 10)
            .Select(d => new DateTime(2026, 3, 10).AddDays(d))
            .First(d => DemoDeviceLifecycle.ChangeTimeOn(d, "Insulin Change") is not null);
        var changeAt = DemoDeviceLifecycle.ChangeTimeOn(day, "Insulin Change")!.Value;

        var before = DemoDeviceStatusGenerator.ReservoirAt(changeAt.AddMinutes(-30));
        var after = DemoDeviceStatusGenerator.ReservoirAt(changeAt.AddMinutes(30));

        after.Should().BeGreaterThan(before, "the reservoir refills at an Insulin Change");
        before.Should().BeLessThan(200 - 60, "three days of use drain a meaningful amount");
    }

    [Fact]
    public void DeviceStatus_UploaderChargesOvernightAndDrainsByEvening()
    {
        var day = new DateTime(2026, 3, 17);

        DemoDeviceStatusGenerator.IsUploaderCharging(day.AddHours(3)).Should().BeTrue();
        DemoDeviceStatusGenerator.IsUploaderCharging(day.AddHours(14)).Should().BeFalse();
        DemoDeviceStatusGenerator.UploaderBatteryAt(day.AddHours(8))
            .Should().BeGreaterThan(DemoDeviceStatusGenerator.UploaderBatteryAt(day.AddHours(22)));
    }

    [Fact]
    public void TherapyProfile_ScheduledRateFollowsTheBlocks()
    {
        const double baseRate = 1.0;

        DemoTherapyProfile.ScheduledRateAt(new DateTime(2026, 3, 17, 0, 30, 0), baseRate).Should().Be(0.9);
        DemoTherapyProfile.ScheduledRateAt(new DateTime(2026, 3, 17, 5, 0, 0), baseRate).Should().Be(1.15);
        DemoTherapyProfile.ScheduledRateAt(new DateTime(2026, 3, 17, 12, 30, 0), baseRate).Should().Be(1.1);
        DemoTherapyProfile.ScheduledRateAt(new DateTime(2026, 3, 17, 23, 0, 0), baseRate).Should().Be(0.9);
    }

    [Fact]
    public void TherapyProfile_BuildsACompleteNightscoutProfile()
    {
        var profile = DemoTherapyProfile.BuildProfile(new DemoModeConfiguration(), DateTime.UtcNow);

        profile.DefaultProfile.Should().Be(DemoTherapyProfile.ProfileName);
        var store = profile.Store[DemoTherapyProfile.ProfileName];
        store.Basal.Should().HaveCount(DemoTherapyProfile.BasalBlocks.Count);
        store.Basal.Should().BeInAscendingOrder(b => b.TimeAsSeconds);
        store.CarbRatio.Should().NotBeEmpty();
        store.Sens.Should().NotBeEmpty();
        store.TargetLow.Should().HaveCount(store.TargetHigh.Count);
        store.Basal.Should().OnlyContain(b => b.Value > 0);
    }

    [Fact]
    public void LifestyleSeeds_PumpModeSpansAreContiguousAndNonOverlapping()
    {
        var spans = DemoLifestyleSeeds.BuildSpans(DateTime.Now.Date, 30);
        var pumpModes = spans
            .Where(s => s.Category == StateSpanCategory.PumpMode)
            .OrderBy(s => s.StartLocal)
            .ToList();

        pumpModes.Should().NotBeEmpty();
        pumpModes[^1].EndLocal.Should().BeNull("the current mode is still active");
        pumpModes.Zip(pumpModes.Skip(1))
            .Should().OnlyContain(pair => pair.First.EndLocal == pair.Second.StartLocal,
                "pump mode is a continuous state — every span hands over to the next");
    }

    [Fact]
    public void LifestyleSeeds_ExerciseArtifactsAlignWithTheWorkoutWindow()
    {
        var today = DateTime.Now.Date;
        var spans = DemoLifestyleSeeds.BuildSpans(today, 60);

        var exerciseDays = Enumerable.Range(1, 60)
            .Select(d => today.AddDays(-d))
            .Where(d => DayScenarios.For(d) == DayScenario.Exercise)
            .ToList();
        exerciseDays.Should().NotBeEmpty("60 days must contain exercise days");

        foreach (var day in exerciseDays)
        {
            var (start, _) = DemoHealthDataGenerator.WorkoutWindowFor(day);
            spans.Should().Contain(s =>
                s.Category == StateSpanCategory.Override
                && s.StartLocal == start.AddMinutes(-10),
                $"exercise day {day:yyyy-MM-dd} carries a workout override at the workout window");
        }
    }

    [Fact]
    public void LifestyleSeeds_ProfileAndTargetSpansCarryDecomposerShapedMetadata()
    {
        // Resolvers read the profile name and target values from metadata, not
        // from State — a name-in-State span silently resolves as "Default".
        var spans = DemoLifestyleSeeds.BuildSpans(DateTime.Now.Date, 60);

        var profile = spans.Single(s => s.Category == StateSpanCategory.Profile);
        profile.State.Should().Be(nameof(ProfileState.Active));
        profile.Metadata.Should().ContainKey("profileName")
            .WhoseValue.Should().Be(DemoTherapyProfile.ProfileName);

        var targets = spans.Where(s => s.Category == StateSpanCategory.TemporaryTarget).ToList();
        targets.Should().NotBeEmpty("60 days contain exercise days with temp targets");
        targets.Should().OnlyContain(t =>
            t.State == nameof(TemporaryTargetState.Active)
            && t.Metadata != null
            && t.Metadata.ContainsKey("targetTop")
            && t.Metadata.ContainsKey("targetBottom"));
    }

    [Fact]
    public void LifestyleSeeds_SpansNeverStartInTheFuture()
    {
        var spans = DemoLifestyleSeeds.BuildSpans(DateTime.Now.Date, 30);

        spans.Should().NotBeEmpty();
        spans.Should().OnlyContain(s => s.StartLocal < DateTime.Now);
        spans.Should().OnlyContain(s => s.EndLocal == null || s.EndLocal > s.StartLocal);
    }

    [Fact]
    public void LifestyleSeeds_WeightIsPlausibleAndDeterministic()
    {
        var day = new DateTime(2026, 3, 15);

        DemoLifestyleSeeds.WeightKgOn(day).Should().BeInRange(70, 85);
        DemoLifestyleSeeds.WeightKgOn(day).Should().Be(DemoLifestyleSeeds.WeightKgOn(day));
    }

    [Fact]
    public void LifestyleSeeds_MealFoodsResolveForEveryGeneratedMealName()
    {
        var day = new DateTime(2026, 3, 17);
        foreach (var meal in new[] { "Breakfast", "Lunch", "Dinner", "Snack" })
        {
            DemoLifestyleSeeds.MealFoodsFor(day, meal).Should().NotBeEmpty(
                $"the food library must cover generated {meal} meals");
        }
    }

    [Fact]
    public void RealtimeDeviceStatus_TracksTreatmentsAcrossTicks()
    {
        var generator = CreateGenerator();
        var entry = new Entry
        {
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Date = DateTime.UtcNow,
            Sgv = 150,
            Mgdl = 150,
        };

        var bolus = new Treatment
        {
            EventType = "Correction Bolus",
            Insulin = 2.0,
            Mills = entry.Mills,
        };

        var idle = generator.GenerateCurrentDeviceStatus(entry, []);
        var afterBolus = generator.GenerateCurrentDeviceStatus(entry, [bolus]);

        idle.OpenAps!.Iob!.Iob.Should().Be(0);
        afterBolus.OpenAps!.Iob!.Iob.Should().BeGreaterThan(1.5, "the bolus lands in IOB");
        afterBolus.Pump!.Reservoir.Should().NotBeNull();
        afterBolus.Uploader!.Battery.Should().BeInRange(20, 100);
    }
}
