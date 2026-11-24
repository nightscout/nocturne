using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

[Trait("Category", "Unit")]
public class TrackerMapperTests
{
    [Theory]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    public void AStoredEventListThatIsNoListOfNames_ReadsAsNone(string stored)
    {
        var definition = TrackerMapper.ToDomain(new TrackerDefinitionEntity
        {
            Name = "Site",
            TriggerEventTypes = stored,
            EndEventTypes = stored,
            Covers = stored,
        });

        definition.TriggerEventTypes.Should().BeEmpty();
        definition.EndEventTypes.Should().BeEmpty();
        definition.Covers.Should().BeEmpty();
    }

    [Fact]
    public void AStoredEventList_IsReadAsStored_SoANameNoLongerAcceptedStaysVisibleToTheRules()
    {
        var definition = TrackerMapper.ToDomain(new TrackerDefinitionEntity
        {
            TriggerEventTypes = """["Pump Suspend","sensor start",null]""",
        });

        definition.TriggerEventTypes.Should().Equal("Pump Suspend", "sensor start");
    }

    [Fact]
    public void ADefinition_RoundTripsThroughItsRow()
    {
        var definition = TrackerDefinition.Restore(
            Guid.NewGuid(),
            "Pod",
            TrackerCategory.Pod,
            TrackerMode.Duration,
            createdBy: "owner",
            description: "Left arm",
            icon: "hexagon",
            triggerEventTypes: ["Pod Change", "Site Change"],
            endEventTypes: ["Pod Deactivated"],
            covers: new HashSet<PumpCapability> { PumpCapability.Power, PumpCapability.Site },
            deviceComponent: "omnipod-5/pod",
            triggerNotesContains: "left",
            lifespanHours: 72,
            lowReservoirUnits: 10,
            lowReservoirUrgency: NotificationUrgency.Hazard,
            isFavorite: true,
            dashboardVisibility: DashboardVisibility.Warn,
            startEventType: "Pod Change",
            completionEventType: "Pod Deactivated",
            notificationThresholds: [new(Guid.Empty, NotificationUrgency.Warn, -24, "A day left", 0)]);

        var row = TrackerMapper.ToEntity(definition);
        row.Covers.Should().Be("""["Site","Power"]""");

        row.Id = definition.Id;

        TrackerMapper.ToDomain(row).Should().BeEquivalentTo(definition, o => o.Excluding(d => d.CreatedAt));
    }

    [Fact]
    public void Thresholds_AreReadInTheirDisplayOrder()
    {
        var row = new TrackerDefinitionEntity
        {
            NotificationThresholds =
            [
                new() { Hours = 240, DisplayOrder = 1 },
                new() { Hours = -24, DisplayOrder = 0 },
            ],
        };

        TrackerMapper.ToDomain(row).NotificationThresholds.Select(t => t.Hours).Should().Equal(-24, 240);
    }
}
