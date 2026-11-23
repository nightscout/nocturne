using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.Trackers;

[Trait("Category", "Unit")]
public class TrackerDefinitionTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private static TrackerDefinitionResult Create(TrackerDefinitionDraft draft) =>
        TrackerDefinition.Create(draft, "owner", Now);

    private static TrackerNotificationThreshold Threshold(int hours) =>
        new(Guid.Empty, NotificationUrgency.Warn, hours, null, 0);

    private static void ShouldBreak(TrackerDefinitionResult result, TrackerDefinitionRule rule, string message)
    {
        result.Definition.Should().BeNull();
        result.Error.Should().Be(new TrackerDefinitionError(rule, message));
    }

    // Name

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ADraftNamedNothing_IsRefused(string name) =>
        ShouldBreak(
            Create(new() { Name = name, Category = TrackerCategory.Custom }),
            TrackerDefinitionRule.Name,
            "Name is required");

    [Fact]
    public void ANamelessDraftBreakingAnotherRule_ReportsTheName() =>
        Create(new() { Mode = TrackerMode.Event, LifespanHours = 24 }).Error!.Rule.Should().Be(TrackerDefinitionRule.Name);

    // Thresholds

    [Fact]
    public void ADurationTrackersNegativeThreshold_InsideItsLifespan_IsKept()
    {
        var result = Create(new() { Name = "Sensor", LifespanHours = 240, NotificationThresholds = [Threshold(-24)] });

        result.Error.Should().BeNull();
        result.Definition!.NotificationThresholds.Should().ContainSingle().Which.Hours.Should().Be(-24);
    }

    [Fact]
    public void ADurationTrackersNegativeThreshold_WithNoLifespan_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Sensor", NotificationThresholds = [Threshold(-24)] }),
            TrackerDefinitionRule.Thresholds,
            "Negative thresholds require a lifespan to be set");

    [Theory]
    [InlineData(-72)]
    [InlineData(-100)]
    public void ADurationTrackersNegativeThreshold_AtOrPastItsLifespan_IsRefused(int hours) =>
        ShouldBreak(
            Create(new() { Name = "Site", LifespanHours = 72, NotificationThresholds = [Threshold(hours)] }),
            TrackerDefinitionRule.Thresholds,
            $"Negative threshold {hours} exceeds tracker lifespan of 72 hours");

    [Fact]
    public void AnEventTrackersNegativeThreshold_CountsBackFromItsTime_AndNeedsNoLifespan() =>
        Create(new() { Name = "Visit", Mode = TrackerMode.Event, NotificationThresholds = [Threshold(-24)] })
            .Error.Should().BeNull();

    // Lifespan

    [Fact]
    public void AnEventTrackerWithALifespan_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Visit", Mode = TrackerMode.Event, LifespanHours = 24 }),
            TrackerDefinitionRule.Lifespan,
            "Event mode trackers should not have a lifespan");

    // Covers

    [Fact]
    public void Covers_AreCanonicalisedAndBlanksDropped()
    {
        var definition = Create(new() { Name = "Pod", Covers = ["", "power", "Site", "site"] }).Definition!;

        definition.Covers.Should().BeEquivalentTo([PumpCapability.Site, PumpCapability.Power]);
    }

    [Fact]
    public void ACoveredNameThatIsNoCapability_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Pod", Covers = ["Drive"] }),
            TrackerDefinitionRule.Covers,
            "'Drive' is not something a tracker can cover");

    // Low reservoir

    [Theory]
    [InlineData(TrackerCategory.Reservoir, null)]
    [InlineData(TrackerCategory.Pod, null)]
    [InlineData(TrackerCategory.Consumable, "Reservoir")]
    public void ALowReservoirLevel_OnATrackerThatHoldsInsulin_IsKept(TrackerCategory category, string? covers)
    {
        var result = Create(new()
        {
            Name = "Reservoir", Category = category, Covers = covers is null ? null : [covers], LowReservoirUnits = 20,
        });

        result.Definition!.LowReservoirUnits.Should().Be(20);
    }

    [Fact]
    public void ALowReservoirLevel_OnATrackerThatHoldsNoInsulin_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Site", Category = TrackerCategory.Cannula, LowReservoirUnits = 20 }),
            TrackerDefinitionRule.LowReservoir,
            "Low reservoir units apply only to trackers of a reservoir or of hardware that holds insulin");

    [Theory]
    [InlineData(0, "Low reservoir units must be greater than zero")]
    [InlineData(-5, "Low reservoir units must be greater than zero")]
    [InlineData(1000.5, "Low reservoir units must be at most 1000")]
    public void ALowReservoirLevelOutOfRange_IsRefused(double units, string message) =>
        ShouldBreak(
            Create(new() { Name = "Reservoir", Category = TrackerCategory.Reservoir, LowReservoirUnits = units }),
            TrackerDefinitionRule.LowReservoir,
            message);

    [Fact]
    public void ALowReservoirLevelOfExactly1000_IsKept() =>
        Create(new() { Name = "Reservoir", Category = TrackerCategory.Reservoir, LowReservoirUnits = 1000 })
            .Error.Should().BeNull();

    // Triggers and ends

    [Fact]
    public void Triggers_AreCanonicalisedDeduplicatedAndBlanksDropped() =>
        Create(new() { Name = "Sensor", TriggerEventTypes = ["sensor start", "", "Sensor Start"] })
            .Definition!.TriggerEventTypes.Should().Equal("Sensor Start");

    [Fact]
    public void ATriggerNoTrackerCanRestartOn_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Sensor", TriggerEventTypes = ["Pump Suspend"] }),
            TrackerDefinitionRule.Triggers,
            "'Pump Suspend' is not an event a tracker can restart on");

    [Fact]
    public void Ends_AreCanonicalised() =>
        Create(new() { Name = "Pod", EndEventTypes = ["pod deactivated", " "] })
            .Definition!.EndEventTypes.Should().Equal("Pod Deactivated");

    [Fact]
    public void AnEndThatIsNotAnEnd_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Pod", EndEventTypes = ["Site Change"] }),
            TrackerDefinitionRule.Ends,
            "'Site Change' is not an event a tracker can end on");

    // Start and completion events

    [Fact]
    public void StartAndCompletionEvents_AreCanonicalised_AndBlankIsNone()
    {
        var definition = Create(new() { Name = "Sensor", StartEventType = "sensor start", CompletionEventType = " " }).Definition!;

        definition.StartEventType.Should().Be("Sensor Start");
        definition.CompletionEventType.Should().BeNull();
    }

    [Fact]
    public void AStartEventThatIsNoDeviceEvent_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Sensor", StartEventType = "Meal Bolus" }),
            TrackerDefinitionRule.EventTypes,
            "StartEventType 'Meal Bolus' is not a device event");

    [Fact]
    public void ACompletionEventThatIsNoDeviceEvent_IsRefused() =>
        ShouldBreak(
            Create(new() { Name = "Sensor", CompletionEventType = "Note" }),
            TrackerDefinitionRule.EventTypes,
            "CompletionEventType 'Note' is not a device event");

    // Order and defaults

    [Fact]
    public void ADraftBreakingSeveralRules_ReportsTheFirstInRuleOrder() =>
        Create(new()
        {
            Name = "Everything",
            Mode = TrackerMode.Event,
            LifespanHours = 24,
            Covers = ["Drive"],
            TriggerEventTypes = ["Pump Suspend"],
            StartEventType = "Meal Bolus",
        }).Error!.Rule.Should().Be(TrackerDefinitionRule.Lifespan);

    [Fact]
    public void ADraft_TakesItsCreatorTimeAndDefaults()
    {
        var definition = Create(new() { Name = "Custom", TriggerNotesContains = "  left " }).Definition!;

        definition.CreatedBy.Should().Be("owner");
        definition.CreatedAt.Should().Be(Now);
        definition.Icon.Should().Be(TrackerDefinition.DefaultIcon);
        definition.TriggerNotesContains.Should().Be("left");
        definition.TriggerEventTypes.Should().BeEmpty();
        definition.Covers.Should().BeEmpty();
    }

    // Revise

    private static TrackerDefinition Stored(
        int? lifespanHours = 72,
        IReadOnlyList<TrackerNotificationThreshold>? thresholds = null,
        string? startEventType = null,
        TrackerCategory category = TrackerCategory.Reservoir,
        double? lowReservoirUnits = null) =>
        TrackerDefinition.Restore(
            Guid.NewGuid(),
            "Reservoir",
            category,
            createdBy: "owner",
            lifespanHours: lifespanHours,
            triggerEventTypes: ["Insulin Change"],
            triggerNotesContains: "left",
            startEventType: startEventType,
            lowReservoirUnits: lowReservoirUnits,
            notificationThresholds: thresholds);

    [Fact]
    public void ARevision_KeepsWhatTheEditLeavesNull()
    {
        var stored = Stored();

        var revised = stored.Revise(new() { Name = "Renamed" }).Definition!;

        revised.Should().BeEquivalentTo(stored, o => o.Excluding(d => d.Name));
        revised.Name.Should().Be("Renamed");
    }

    [Fact]
    public void ARevisionShorteningTheLifespanPastAStoredThreshold_IsRefused() =>
        ShouldBreak(
            Stored(lifespanHours: 72, thresholds: [Threshold(-24)]).Revise(new() { LifespanHours = 24 }),
            TrackerDefinitionRule.Thresholds,
            "Negative threshold -24 exceeds tracker lifespan of 24 hours");

    [Fact]
    public void ARevisionPostingThresholds_ReplacesTheStoredOnes()
    {
        var revised = Stored(thresholds: [Threshold(-24)]).Revise(new() { NotificationThresholds = [Threshold(48)] }).Definition!;

        revised.NotificationThresholds.Should().ContainSingle().Which.Hours.Should().Be(48);
    }

    [Fact]
    public void ARevisionPostingATriggerList_TakesTheNotesFilterWithIt()
    {
        var revised = Stored().Revise(new() { TriggerEventTypes = ["Reservoir Change"] }).Definition!;

        revised.TriggerEventTypes.Should().Equal("Reservoir Change");
        revised.TriggerNotesContains.Should().BeNull();
    }

    [Fact]
    public void ARevisionRepostingAStoredEventThatIsNoDeviceEvent_KeepsIt() =>
        Stored(startEventType: "Note").Revise(new() { StartEventType = "note" })
            .Definition!.StartEventType.Should().Be("Note");

    [Fact]
    public void ARevisionChangingAStoredEventToOneThatIsNoDeviceEvent_IsRefused() =>
        ShouldBreak(
            Stored(startEventType: "Note").Revise(new() { StartEventType = "Meal Bolus" }),
            TrackerDefinitionRule.EventTypes,
            "StartEventType 'Meal Bolus' is not a device event");

    [Fact]
    public void ARevisionWithNoEventType_ClearsIt() =>
        Stored(startEventType: "Sensor Start").Revise(new() { Name = "Renamed" })
            .Definition!.StartEventType.Should().BeNull();

    [Fact]
    public void ARevisionWithNoLowReservoirLevel_ClearsIt() =>
        Stored(lowReservoirUnits: 20).Revise(new() { Name = "Renamed" })
            .Definition!.LowReservoirUnits.Should().BeNull();

    [Fact]
    public void ARevisionTurningAReservoirIntoATrackerThatHoldsNoInsulin_RefusesItsLevel() =>
        ShouldBreak(
            Stored(lowReservoirUnits: 20).Revise(new() { Category = TrackerCategory.Cannula, LowReservoirUnits = 20 }),
            TrackerDefinitionRule.LowReservoir,
            "Low reservoir units apply only to trackers of a reservoir or of hardware that holds insulin");

    [Fact]
    public void ARevisionToEventModePostingNoLifespan_DropsTheStoredLifespan()
    {
        var stored = Stored(lifespanHours: 72);

        var revised = stored.Revise(new() { Mode = TrackerMode.Event }).Definition!;

        revised.Mode.Should().Be(TrackerMode.Event);
        revised.LifespanHours.Should().BeNull();
    }

    [Fact]
    public void ARevisionToEventModePostingALifespan_IsRefused() =>
        ShouldBreak(
            Stored().Revise(new() { Mode = TrackerMode.Event, LifespanHours = 24 }),
            TrackerDefinitionRule.Lifespan,
            "Event mode trackers should not have a lifespan");

    [Fact]
    public void ARevisionOfAnEventTrackerPostingALifespan_IsRefused() =>
        ShouldBreak(
            TrackerDefinition.Restore(Guid.NewGuid(), "Visit", mode: TrackerMode.Event).Revise(new() { LifespanHours = 24 }),
            TrackerDefinitionRule.Lifespan,
            "Event mode trackers should not have a lifespan");

    [Fact]
    public void ARevisionOfADurationTrackerPostingNoLifespan_KeepsIt() =>
        Stored(lifespanHours: 72).Revise(new() { Name = "Renamed" }).Definition!.LifespanHours.Should().Be(72);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ARevisionNamingTheTrackerNothing_IsRefused(string name) =>
        ShouldBreak(Stored().Revise(new() { Name = name }), TrackerDefinitionRule.Name, "Name is required");

    // Starting a run

    [Theory]
    [InlineData(TrackerMode.Event, false, "Event mode trackers require a ScheduledAt datetime")]
    [InlineData(TrackerMode.Duration, true, "Duration mode trackers should not have a ScheduledAt datetime")]
    [InlineData(TrackerMode.Event, true, null)]
    [InlineData(TrackerMode.Duration, false, null)]
    public void ARunStarts_OnlyWithTheScheduleItsModeTakes(TrackerMode mode, bool scheduled, string? refusal) =>
        TrackerDefinition.Restore(Guid.NewGuid(), "Tracker", mode: mode)
            .CannotStart(scheduled ? Now : null)
            .Should().Be(refusal);
}
