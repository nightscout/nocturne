using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class PumpSnapshotMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var model = new PumpSnapshot
        {
            Id = id,
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "omnipod-dash",
            LegacyId = "pump123",
            Manufacturer = "Insulet",
            Model = "Omnipod DASH",
            Reservoir = 150.5,
            ReservoirDisplay = "150.5U",
            BatteryPercent = 85,
            BatteryVoltage = 3.7,
            Bolusing = false,
            Suspended = false,
            PumpStatus = "normal",
            Clock = "2024-01-15T10:30:00Z",
        };

        var entity = PumpSnapshotMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(-300);
        entity.Device.Should().Be("omnipod-dash");
        entity.LegacyId.Should().Be("pump123");
        entity.Manufacturer.Should().Be("Insulet");
        entity.Model.Should().Be("Omnipod DASH");
        entity.Reservoir.Should().Be(150.5);
        entity.ReservoirDisplay.Should().Be("150.5U");
        entity.BatteryPercent.Should().Be(85);
        entity.BatteryVoltage.Should().Be(3.7);
        entity.Bolusing.Should().BeFalse();
        entity.Suspended.Should().BeFalse();
        entity.PumpStatus.Should().Be("normal");
        entity.Clock.Should().Be("2024-01-15T10:30:00Z");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new PumpSnapshot { Mills = 1700000000000 };

        var entity = PumpSnapshotMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new PumpSnapshotEntity
        {
            Id = id,
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "omnipod-dash",
            LegacyId = "pump123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt,
            Manufacturer = "Insulet",
            Model = "Omnipod DASH",
            Reservoir = 150.5,
            ReservoirDisplay = "150.5U",
            BatteryPercent = 85,
            BatteryVoltage = 3.7,
            Bolusing = false,
            Suspended = false,
            PumpStatus = "normal",
            Clock = "2024-01-15T10:30:00Z",
        };

        var model = PumpSnapshotMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.UtcOffset.Should().Be(-300);
        model.Device.Should().Be("omnipod-dash");
        model.LegacyId.Should().Be("pump123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
        model.Manufacturer.Should().Be("Insulet");
        model.Model.Should().Be("Omnipod DASH");
        model.Reservoir.Should().Be(150.5);
        model.ReservoirDisplay.Should().Be("150.5U");
        model.BatteryPercent.Should().Be(85);
        model.BatteryVoltage.Should().Be(3.7);
        model.Bolusing.Should().BeFalse();
        model.Suspended.Should().BeFalse();
        model.PumpStatus.Should().Be("normal");
        model.Clock.Should().Be("2024-01-15T10:30:00Z");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new PumpSnapshotEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
        };

        var model = new PumpSnapshot
        {
            Mills = 1700000000000,
            UtcOffset = 60,
            Device = "tandem-tslim",
            LegacyId = "upd789",
            Manufacturer = "Tandem",
            Model = "t:slim X2",
            Reservoir = 200.0,
            ReservoirDisplay = "200U",
            BatteryPercent = 92,
            BatteryVoltage = 4.1,
            Bolusing = true,
            Suspended = false,
            PumpStatus = "bolusing",
            Clock = "2024-06-15T14:00:00Z",
        };

        PumpSnapshotMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(60);
        entity.Device.Should().Be("tandem-tslim");
        entity.LegacyId.Should().Be("upd789");
        entity.Manufacturer.Should().Be("Tandem");
        entity.Model.Should().Be("t:slim X2");
        entity.Reservoir.Should().Be(200.0);
        entity.ReservoirDisplay.Should().Be("200U");
        entity.BatteryPercent.Should().Be(92);
        entity.BatteryVoltage.Should().Be(4.1);
        entity.Bolusing.Should().BeTrue();
        entity.Suspended.Should().BeFalse();
        entity.PumpStatus.Should().Be("bolusing");
        entity.Clock.Should().Be("2024-06-15T14:00:00Z");
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }
}
