using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class DeviceMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var model = new Device
        {
            Id = id,
            Category = DeviceCategory.InsulinPump,
            Type = "Omnipod DASH",
            Serial = "SN-12345",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime,
        };

        var entity = DeviceMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Category.Should().Be("InsulinPump");
        entity.Type.Should().Be("Omnipod DASH");
        entity.Serial.Should().Be("SN-12345");
        entity.FirstSeenTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.LastSeenTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new Device
        {
            Category = DeviceCategory.InsulinPump,
            Type = "Medtronic 780G",
            Serial = "ABC-999",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        var entity = DeviceMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_DefaultStrings_MapCorrectly()
    {
        var model = new Device
        {
            Category = DeviceCategory.InsulinPump,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        var entity = DeviceMapper.ToEntity(model);

        entity.Type.Should().BeEmpty();
        entity.Serial.Should().BeEmpty();
        entity.Category.Should().Be("InsulinPump");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var entity = new DeviceEntity
        {
            Id = id,
            Category = "InsulinPump",
            Type = "Omnipod DASH",
            Serial = "SN-12345",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime,
        };

        var model = DeviceMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Category.Should().Be(DeviceCategory.InsulinPump);
        model.Type.Should().Be("Omnipod DASH");
        model.Serial.Should().Be("SN-12345");
        model.FirstSeenMills.Should().Be(1700000000000);
        model.LastSeenMills.Should().Be(1700001000000);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_UnknownCategory_DefaultsToInsulinPump()
    {
        var entity = new DeviceEntity
        {
            Id = Guid.CreateVersion7(),
            Category = "UnknownCategory",
            Type = "Test",
            Serial = "SN-001",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        var model = DeviceMapper.ToDomainModel(entity);

        model.Category.Should().Be(DeviceCategory.InsulinPump);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFields()
    {
        var originalId = Guid.CreateVersion7();
        var entity = new DeviceEntity
        {
            Id = originalId,
            Category = "InsulinPump",
            Type = "OldType",
            Serial = "OldSerial",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(2000).UtcDateTime,
        };

        var model = new Device
        {
            Category = DeviceCategory.CGM,
            Type = "Omnipod DASH",
            Serial = "SN-99999",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime,
        };

        DeviceMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.Category.Should().Be("CGM");
        entity.Type.Should().Be("Omnipod DASH");
        entity.Serial.Should().Be("SN-99999");
        entity.FirstSeenTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.LastSeenTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_DoesNotChangeId()
    {
        var originalId = Guid.CreateVersion7();
        var entity = new DeviceEntity
        {
            Id = originalId,
            Category = "InsulinPump",
            Type = "OldType",
            Serial = "OldSerial",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(2000).UtcDateTime,
        };

        var model = new Device
        {
            Id = Guid.CreateVersion7(),
            Category = DeviceCategory.InsulinPump,
            Type = "NewType",
            Serial = "NewSerial",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(3000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(4000).UtcDateTime,
        };

        DeviceMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var original = new Device
        {
            Id = id,
            Category = DeviceCategory.InsulinPump,
            Type = "Medtronic 780G",
            Serial = "MED-54321",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700005000000).UtcDateTime,
        };

        var entity = DeviceMapper.ToEntity(original);
        var roundTripped = DeviceMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.Category.Should().Be(original.Category);
        roundTripped.Type.Should().Be(original.Type);
        roundTripped.Serial.Should().Be(original.Serial);
        roundTripped.FirstSeenMills.Should().Be(original.FirstSeenMills);
        roundTripped.LastSeenMills.Should().Be(original.LastSeenMills);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MalformedAdditionalPropertiesJson_YieldsNoAdditionalProperties()
    {
        var entity = new DeviceEntity
        {
            Id = Guid.CreateVersion7(),
            Category = "InsulinPump",
            Type = "Omnipod DASH",
            Serial = "SN-12345",
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            AdditionalPropertiesJson = """{"unterminated":""",
        };

        var model = DeviceMapper.ToDomainModel(entity);

        model.AdditionalProperties.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }
}
