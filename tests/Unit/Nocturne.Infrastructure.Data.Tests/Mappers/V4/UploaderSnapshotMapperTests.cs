using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class UploaderSnapshotMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var model = new UploaderSnapshot
        {
            Id = id,
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "iphone-14",
            LegacyId = "upl123",
            Name = "Johns iPhone",
            Battery = 75,
            BatteryVoltage = 3.8,
            IsCharging = true,
            Temperature = 36.5,
            Type = "phone",
        };

        var entity = UploaderSnapshotMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(-300);
        entity.Device.Should().Be("iphone-14");
        entity.LegacyId.Should().Be("upl123");
        entity.Name.Should().Be("Johns iPhone");
        entity.Battery.Should().Be(75);
        entity.BatteryVoltage.Should().Be(3.8);
        entity.IsCharging.Should().BeTrue();
        entity.Temperature.Should().Be(36.5);
        entity.Type.Should().Be("phone");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new UploaderSnapshot { Mills = 1700000000000 };

        var entity = UploaderSnapshotMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new UploaderSnapshotEntity
        {
            Id = id,
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "iphone-14",
            LegacyId = "upl123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt,
            Name = "Johns iPhone",
            Battery = 75,
            BatteryVoltage = 3.8,
            IsCharging = true,
            Temperature = 36.5,
            Type = "phone",
        };

        var model = UploaderSnapshotMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.UtcOffset.Should().Be(-300);
        model.Device.Should().Be("iphone-14");
        model.LegacyId.Should().Be("upl123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
        model.Name.Should().Be("Johns iPhone");
        model.Battery.Should().Be(75);
        model.BatteryVoltage.Should().Be(3.8);
        model.IsCharging.Should().BeTrue();
        model.Temperature.Should().Be(36.5);
        model.Type.Should().Be("phone");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new UploaderSnapshotEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
        };

        var model = new UploaderSnapshot
        {
            Mills = 1700000000000,
            UtcOffset = 60,
            Device = "pixel-8",
            LegacyId = "upd456",
            Name = "Janes Pixel",
            Battery = 45,
            BatteryVoltage = 3.6,
            IsCharging = false,
            Temperature = 38.0,
            Type = "android",
        };

        UploaderSnapshotMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(60);
        entity.Device.Should().Be("pixel-8");
        entity.LegacyId.Should().Be("upd456");
        entity.Name.Should().Be("Janes Pixel");
        entity.Battery.Should().Be(45);
        entity.BatteryVoltage.Should().Be(3.6);
        entity.IsCharging.Should().BeFalse();
        entity.Temperature.Should().Be(38.0);
        entity.Type.Should().Be("android");
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }
}
