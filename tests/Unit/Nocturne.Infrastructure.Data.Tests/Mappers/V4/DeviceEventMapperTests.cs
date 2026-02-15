using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class DeviceEventMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var model = new DeviceEvent
        {
            Id = id,
            Mills = 1700000000000,
            EventType = DeviceEventType.SiteChange,
            Notes = "Left arm",
            Device = "omnipod",
            App = "loop",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "device-event-123"
        };

        var entity = DeviceEventMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.EventType.Should().Be("SiteChange");
        entity.Notes.Should().Be("Left arm");
        entity.Device.Should().Be("omnipod");
        entity.App.Should().Be("loop");
        entity.UtcOffset.Should().Be(-300);
        entity.DataSource.Should().Be("nightscout");
        entity.CorrelationId.Should().Be(correlationId);
        entity.LegacyId.Should().Be("device-event-123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new DeviceEvent { Mills = 1700000000000, EventType = DeviceEventType.SensorStart };

        var entity = DeviceEventMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AllEventTypeValues_MapCorrectly()
    {
        foreach (var eventType in Enum.GetValues<DeviceEventType>())
        {
            var model = new DeviceEvent { Mills = 1700000000000, EventType = eventType };
            var entity = DeviceEventMapper.ToEntity(model);
            entity.EventType.Should().Be(eventType.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullNotes_MapsToNull()
    {
        var model = new DeviceEvent
        {
            Mills = 1700000000000,
            EventType = DeviceEventType.SensorStart,
            Notes = null
        };

        var entity = DeviceEventMapper.ToEntity(model);

        entity.Notes.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new DeviceEventEntity
        {
            Id = id,
            Mills = 1700000000000,
            EventType = "SiteChange",
            Notes = "Left arm",
            Device = "omnipod",
            App = "loop",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "device-event-123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt
        };

        var model = DeviceEventMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.EventType.Should().Be(DeviceEventType.SiteChange);
        model.Notes.Should().Be("Left arm");
        model.Device.Should().Be("omnipod");
        model.App.Should().Be("loop");
        model.UtcOffset.Should().Be(-300);
        model.DataSource.Should().Be("nightscout");
        model.CorrelationId.Should().Be(correlationId);
        model.LegacyId.Should().Be("device-event-123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_InvalidEventType_FallsBackToSiteChange()
    {
        var entity = new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            EventType = "InvalidType"
        };

        var model = DeviceEventMapper.ToDomainModel(entity);

        model.EventType.Should().Be(DeviceEventType.SiteChange);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_AllEventTypeStrings_ParseCorrectly()
    {
        foreach (var eventType in Enum.GetValues<DeviceEventType>())
        {
            var entity = new DeviceEventEntity
            {
                Id = Guid.CreateVersion7(),
                Mills = 1700000000000,
                EventType = eventType.ToString()
            };

            var model = DeviceEventMapper.ToDomainModel(entity);

            model.EventType.Should().Be(eventType);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new DeviceEventEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
            EventType = "SensorStart"
        };

        var model = new DeviceEvent
        {
            EventType = DeviceEventType.PumpBatteryChange,
            Notes = "Updated notes",
            Mills = 1700000000000,
            Device = "tandem",
            App = "controliq",
            UtcOffset = 60,
            DataSource = "tidepool",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "upd456"
        };

        DeviceEventMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.EventType.Should().Be("PumpBatteryChange");
        entity.Notes.Should().Be("Updated notes");
        entity.Mills.Should().Be(1700000000000);
        entity.Device.Should().Be("tandem");
        entity.App.Should().Be("controliq");
        entity.UtcOffset.Should().Be(60);
        entity.DataSource.Should().Be("tidepool");
        entity.CorrelationId.Should().Be(model.CorrelationId);
        entity.LegacyId.Should().Be("upd456");
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var original = new DeviceEvent
        {
            Id = id,
            Mills = 1700000000000,
            EventType = DeviceEventType.PodChange,
            Notes = "Changed pod",
            Device = "omnipod",
            App = "loop",
            UtcOffset = -480,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "rt789"
        };

        var entity = DeviceEventMapper.ToEntity(original);
        var roundTripped = DeviceEventMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.Mills.Should().Be(original.Mills);
        roundTripped.EventType.Should().Be(original.EventType);
        roundTripped.Notes.Should().Be(original.Notes);
        roundTripped.Device.Should().Be(original.Device);
        roundTripped.App.Should().Be(original.App);
        roundTripped.UtcOffset.Should().Be(original.UtcOffset);
        roundTripped.DataSource.Should().Be(original.DataSource);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);
        roundTripped.LegacyId.Should().Be(original.LegacyId);
    }
}
