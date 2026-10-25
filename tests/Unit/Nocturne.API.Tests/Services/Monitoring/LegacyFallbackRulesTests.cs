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

    private static TrackerDefinitionEntity Definition(
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
        spec.Category.Should().Be(TrackerCategory.Cannula);
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
}
