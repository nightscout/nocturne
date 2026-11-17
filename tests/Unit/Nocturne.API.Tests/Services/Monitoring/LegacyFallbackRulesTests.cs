using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class LegacyFallbackRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    internal static TrackerDefinitionEntity Definition(
        TrackerMode mode = TrackerMode.Duration,
        TrackerCategory category = TrackerCategory.Custom,
        DashboardVisibility visibility = DashboardVisibility.Always,
        params string[] triggers) =>
        new()
        {
            Name = "Tracker",
            Mode = mode,
            Category = category,
            DashboardVisibility = visibility,
            TriggerEventTypes = JsonSerializer.Serialize(triggers),
        };

    [Fact]
    public void ADurationTrackerRestartedByAKindsEvent_CoversIt_WhateverItsCategory()
    {
        var definitions = new[] { Definition(category: TrackerCategory.Custom, triggers: ["Sensor Change"]) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Sensor, definitions).Should().BeTrue();
        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Site, definitions).Should().BeFalse();
    }

    [Fact]
    public void AnEventModeTracker_CoversNothing()
    {
        var definitions = new[] { Definition(mode: TrackerMode.Event, triggers: ["Site Change"]) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Site, definitions).Should().BeFalse();
    }

    [Fact]
    public void ATrackerFollowingOnlySensorStopOrTransmitterInsert_DoesNotCoverTheSensor()
    {
        var definitions = new[] { Definition(triggers: ["Sensor Stop", "Transmitter Sensor Insert"]) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Sensor, definitions).Should().BeFalse();
    }

    [Fact]
    public void APodTracker_CoversTheSite()
    {
        var definitions = new[] { Definition(category: TrackerCategory.Cannula, triggers: ["Pod Change"]) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Site, definitions).Should().BeTrue();
    }

    [Fact]
    public void ATrackerHiddenFromTheDashboard_StillCovers()
    {
        var definitions = new[] { Definition(visibility: DashboardVisibility.Off, triggers: ["Insulin Change"]) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Reservoir, definitions).Should().BeTrue();
    }

    [Fact]
    public void ATrackerWithNoTriggers_CoversOnlyTheKindOfItsCategory()
    {
        var definitions = new[] { Definition(category: TrackerCategory.Sensor) };

        Enum.GetValues<LegacyConsumableKind>()
            .Where(kind => LegacyFallbackRules.IsCovered(kind, definitions))
            .Should().Equal(LegacyConsumableKind.Sensor);
    }

    [Theory]
    [InlineData(TrackerCategory.Cannula, LegacyConsumableKind.Site)]
    [InlineData(TrackerCategory.Reservoir, LegacyConsumableKind.Reservoir)]
    public void EachKindsCategory_CoversItForAHandRunTracker(TrackerCategory category, LegacyConsumableKind kind)
    {
        LegacyFallbackRules.IsCovered(kind, [Definition(category: category)]).Should().BeTrue();
    }

    [Fact]
    public void AMembersPhoneBatteryTracker_DoesNotHideThePumpBattery()
    {
        var phone = Definition(category: TrackerCategory.Battery);
        phone.Name = "Phone battery";

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.PumpBattery, [phone]).Should().BeFalse();
    }


    [Fact]
    public void ACustomTrackerWithNoTriggers_CoversNothing()
    {
        var definitions = new[] { Definition(category: TrackerCategory.Custom) };

        Enum.GetValues<LegacyConsumableKind>()
            .Should().OnlyContain(kind => !LegacyFallbackRules.IsCovered(kind, definitions));
    }

    [Theory]
    [InlineData("pod", "pod", true)]
    [InlineData("pod", "infusion set", false)]
    [InlineData("pod", "generic", true)]
    [InlineData("pod", "no triggers", true)]
    [InlineData("infusion set", "pod", false)]
    [InlineData("infusion set", "infusion set", true)]
    [InlineData("infusion set", "generic", true)]
    [InlineData("infusion set", "no triggers", true)]
    public void ASiteTracker_FollowsASiteSpecOfTheSameHardware(string spec, string tracker, bool follows)
    {
        var specs = new Dictionary<string, ConsumableTrackerSpec>
        {
            ["pod"] = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("omnipod-dash")!).Single(),
            ["infusion set"] = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("dana-i")!)
                .Single(s => s.Category == TrackerCategory.Cannula),
        };
        var trackers = new Dictionary<string, TrackerDefinitionEntity>
        {
            ["pod"] = Definition(category: TrackerCategory.Cannula, triggers: ["Pod Change", "Site Change"]),
            ["infusion set"] = Definition(category: TrackerCategory.Cannula, triggers: ["Site Change", "Cannula Change"]),
            ["generic"] = ConsumableTrackerSpecs.ToDefinition(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site), "member"),
            ["no triggers"] = Definition(category: TrackerCategory.Cannula),
        };

        LegacyFallbackRules.Follows(trackers[tracker], specs[spec]).Should().Be(follows);
    }

    [Fact]
    public void AnEventOlderThanThreeLifespans_IsStale()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site);

        LegacyFallbackRules.IsConfident(
            LegacyConsumableKind.Site, spec, Now.AddHours(-3 * spec.LifespanHours), 0, Now).Should().BeTrue();
        LegacyFallbackRules.IsConfident(
            LegacyConsumableKind.Site, spec, Now.AddHours(-3 * spec.LifespanHours - 1), 0, Now).Should().BeFalse();
    }

    [Fact]
    public void ASingleBatteryChange_IsNotEnough()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.PumpBattery);

        LegacyFallbackRules.IsConfident(
            LegacyConsumableKind.PumpBattery, spec, Now.AddDays(-3), recentCount: 1, Now).Should().BeFalse();
    }

    [Fact]
    public void TwoRecentBatteryChanges_AreEnough()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.PumpBattery);

        LegacyFallbackRules.IsConfident(
            LegacyConsumableKind.PumpBattery, spec, Now.AddDays(-3), recentCount: 2, Now).Should().BeTrue();
    }

    [Fact]
    public void OtherKinds_NeedOnlyOneEvent()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Sensor);

        LegacyFallbackRules.IsConfident(
            LegacyConsumableKind.Sensor, spec, Now.AddDays(-3), recentCount: 0, Now).Should().BeTrue();
    }
}

[Trait("Category", "Unit")]
public class ConsumableTrackerSpecsTests
{
    private static PatientDevice Device(string catalogId, int? rank = null) =>
        new() { CatalogId = catalogId, Rank = rank };

    [Fact]
    public void ARegisteredCgm_SuppliesTheSensorSpec()
    {
        var spec = ConsumableTrackerSpecs.For(LegacyConsumableKind.Sensor, [Device("libre-3")])!;

        spec.Name.Should().Be("FreeStyle Libre 3 sensor");
        spec.LifespanHours.Should().Be(14 * 24);
    }

    [Fact]
    public void ARegisteredPatchPump_SuppliesTheSiteSpec()
    {
        var spec = ConsumableTrackerSpecs.For(LegacyConsumableKind.Site, [Device("omnipod-dash")])!;

        spec.Name.Should().Be("Omnipod DASH pod");
        spec.Category.Should().Be(TrackerCategory.Pod);
    }

    [Fact]
    public void WithoutAMatchingDevice_TheGenericSpecIsUsed()
    {
        var spec = ConsumableTrackerSpecs.For(LegacyConsumableKind.Sensor, [Device("omnipod-dash")]);

        spec.Should().BeEquivalentTo(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Sensor));
    }

    [Fact]
    public void ATransmitterSpec_DoesNotStandForThePumpBattery()
    {
        var spec = ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, [Device("dexcom-g6")]);

        spec.Should().BeEquivalentTo(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.PumpBattery));
    }

    [Theory]
    [InlineData("omnipod-dash")]
    [InlineData("omnipod-5")]
    public void APatchPump_OffersNoReservoir(string catalogId)
    {
        ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, [Device(catalogId)]).Should().BeNull();
    }

    [Fact]
    public void ATubedPumpOrNoPump_OffersTheReservoir()
    {
        ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, [Device("dana-i")])!
            .Category.Should().Be(TrackerCategory.Reservoir);
        ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, [])
            .Should().BeEquivalentTo(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Reservoir));
    }

    [Fact]
    public void TheHighestRankedMatchingDevice_Wins()
    {
        var spec = ConsumableTrackerSpecs.For(
            LegacyConsumableKind.Sensor, [Device("libre-3", rank: 2), Device("dexcom-g6", rank: 1)])!;

        spec.Name.Should().Be("Dexcom G6 sensor");
    }

    [Fact]
    public void EveryGenericSpec_IsRestartedByEveryEventOfItsKind()
    {
        foreach (var kind in Enum.GetValues<LegacyConsumableKind>())
        {
            ConsumableTrackerSpecs.TriggerEventTypes(ConsumableTrackerSpecs.Generic(kind))
                .Should().BeEquivalentTo(ConsumableTrackerSpecs.EventTypes(kind), kind.ToString());
        }
    }

    [Fact]
    public void TheFallbackLadder_WarnsADayBeforeTheEndAndRaisesAHazardAtIt()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Sensor);
        var definition = ConsumableTrackerSpecs.ToDefinition(spec, "user");

        definition.NotificationThresholds
            .Select(t => (t.Urgency, TrackerSchedule.OffsetMinutes(definition.Mode, definition.LifespanHours, t.Hours)))
            .Should().Equal(
                (NotificationUrgency.Warn, (spec.LifespanHours - 24) * 60),
                (NotificationUrgency.Hazard, spec.LifespanHours * 60));
    }

    [Fact]
    public void APodTracker_CoversTheSiteTheInsulinAndTheBattery()
    {
        var definitions = new[] { LegacyFallbackRulesTests.Definition(category: TrackerCategory.Pod) };

        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Site, definitions).Should().BeTrue();
        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Reservoir, definitions).Should().BeTrue();
        LegacyFallbackRules.IsCovered(LegacyConsumableKind.PumpBattery, definitions).Should().BeTrue();
        LegacyFallbackRules.IsCovered(LegacyConsumableKind.Sensor, definitions).Should().BeFalse();
    }

    [Fact]
    public void StatedCoverage_OverridesTheTriggers()
    {
        var definition = LegacyFallbackRulesTests.Definition(category: TrackerCategory.Cannula, triggers: ["Pod Change", "Pump Battery Change"]);
        definition.Covers = """["Site"]""";

        LegacyFallbackRules.Covers(LegacyConsumableKind.Site, definition).Should().BeTrue();
        LegacyFallbackRules.Covers(LegacyConsumableKind.PumpBattery, definition).Should().BeFalse();
        LegacyFallbackRules.Covers(LegacyConsumableKind.Reservoir, definition).Should().BeFalse();
    }

    [Theory]
    [InlineData("omnipod-dash")]
    [InlineData("medtrum-nano")]
    [InlineData("beta-bionics-mint")]
    [InlineData("accu-chek-solo")]
    public void APumpPoweredByItsDisposable_OffersNoPumpBattery(string catalogId)
    {
        ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, [Device(catalogId)]).Should().BeNull();
    }

    [Fact]
    public void WithAPodPumpAndATubedPump_EachKindTakesItsOwnSpec()
    {
        var devices = new[] { Device("omnipod-5", rank: 1), Device("dana-i", rank: 2) };

        ConsumableTrackerSpecs.For(LegacyConsumableKind.Site, devices)!.Name.Should().Be("Omnipod 5 pod");
        ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, devices)!.Name.Should().Be("Dana-i reservoir");
        ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, devices).Should().BeNull();
    }

    [Fact]
    public void ATubedPump_OffersTheGenericPumpBattery()
    {
        ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, [Device("dana-i")])
            .Should().BeEquivalentTo(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.PumpBattery));
    }

    [Fact]
    public void Equil_IsASiteAndAReservoir_NotAPod()
    {
        var site = ConsumableTrackerSpecs.For(LegacyConsumableKind.Site, [Device("equil")])!;
        var reservoir = ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, [Device("equil")])!;

        site.Name.Should().Be("Equil infusion site");
        site.Category.Should().Be(TrackerCategory.Cannula);
        reservoir.Name.Should().Be("Equil reservoir");
    }

    [Fact]
    public void AccuChekSolo_HasAFourDayReservoirAndNoBaseTrackerByDefault()
    {
        var specs = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("accu-chek-solo")!);

        specs.Select(s => (s.Name, s.LifespanHours)).Should().BeEquivalentTo(
            [("Accu-Chek Solo infusion site", 72), ("Accu-Chek Solo reservoir", 96)]);
    }

    [Fact]
    public void Mint_TracksItsCartridgeAsAPodWithItsLongerGrace()
    {
        var spec = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("beta-bionics-mint")!).Single();

        spec.Category.Should().Be(TrackerCategory.Pod);
        spec.Component.Should().Be("beta-bionics-mint/pod");
        spec.Ends.Should().Equal("Pod Deactivated");
        ConsumableTrackerSpecs.Thresholds(spec)
            .Should().ContainSingle(t => t.Urgency == NotificationUrgency.Urgent)
            .Which.Hours.Should().Be(72 + 12 - ConsumableTrackerSpecs.UrgentHoursBeforeStop);
    }

    [Theory]
    [InlineData("dexcom-g7", 10)]
    [InlineData("dexcom-g7-15-day", 15)]
    [InlineData("dexcom-one-plus", 10)]
    public void ADexcomG7Sensor_RaisesAnUrgentAlertBeforeItStops(string catalogId, int days)
    {
        var spec = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById(catalogId)!).Single();

        spec.GraceHours.Should().Be(12);
        ConsumableTrackerSpecs.Thresholds(spec)
            .Should().ContainSingle(t => t.Urgency == NotificationUrgency.Urgent)
            .Which.Hours.Should().Be(days * 24 + 12 - ConsumableTrackerSpecs.UrgentHoursBeforeStop);
    }

    [Fact]
    public void ASensorWithNoGrace_KeepsTheWarnAndHazardLadder()
    {
        var spec = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("dexcom-g6")!)
            .Single(s => s.Category == TrackerCategory.Sensor);

        spec.GraceHours.Should().BeNull();
        ConsumableTrackerSpecs.Thresholds(spec)
            .Select(t => (t.Urgency, t.Hours))
            .Should().Equal(
                (NotificationUrgency.Warn, -ConsumableTrackerSpecs.WarnHoursBeforeEnd),
                (NotificationUrgency.Hazard, 240));
    }

    [Fact]
    public void APodTrackerOfAnotherPodPump_FollowsThePodSpec()
    {
        var medtrum = ConsumableTrackerSpecs.ToDefinition(
            ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("medtrum-nano")!).Single(), "member");
        var omnipod = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("omnipod-5")!).Single();
        var equilSite = ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById("equil")!)
            .Single(s => s.Category == TrackerCategory.Cannula);

        LegacyFallbackRules.Follows(medtrum, omnipod).Should().BeTrue();
        LegacyFallbackRules.Follows(medtrum, equilSite).Should().BeFalse();
    }

    [Fact]
    public void ATrackerOfTheSamePart_FollowsItsSpec_WhateverItCovers()
    {
        var entry = DeviceCatalog.GetById("beta-bionics-mint")!;
        var controller = ConsumableTrackerSpecs.SpecFor(entry, entry.Pump!.Components.Single(c => c.Key == "controller"))!;
        var definition = LegacyFallbackRulesTests.Definition(category: TrackerCategory.Custom);
        definition.DeviceComponent = "beta-bionics-mint/controller";

        LegacyFallbackRules.Follows(definition, controller).Should().BeTrue();
        LegacyFallbackRules.Follows(LegacyFallbackRulesTests.Definition(category: TrackerCategory.Custom), controller).Should().BeFalse();
    }
}
