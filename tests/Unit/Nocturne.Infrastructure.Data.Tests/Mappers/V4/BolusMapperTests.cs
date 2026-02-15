using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using BolusType = Nocturne.Core.Models.V4.BolusType;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class BolusMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var model = new Bolus
        {
            Id = id,
            Mills = 1700000000000,
            Insulin = 5.5,
            Programmed = 6.0,
            Delivered = 5.5,
            BolusType = BolusType.Normal,
            Automatic = false,
            Duration = 0,
            Device = "omnipod",
            App = "loop",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "bolus123"
        };

        var entity = BolusMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.Insulin.Should().Be(5.5);
        entity.Programmed.Should().Be(6.0);
        entity.Delivered.Should().Be(5.5);
        entity.BolusType.Should().Be("Normal");
        entity.Automatic.Should().BeFalse();
        entity.Duration.Should().Be(0);
        entity.Device.Should().Be("omnipod");
        entity.App.Should().Be("loop");
        entity.UtcOffset.Should().Be(-300);
        entity.DataSource.Should().Be("nightscout");
        entity.CorrelationId.Should().Be(correlationId);
        entity.LegacyId.Should().Be("bolus123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new Bolus { Mills = 1700000000000, Insulin = 1.0 };

        var entity = BolusMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullBolusType_MapsToNull()
    {
        var model = new Bolus { Mills = 1700000000000, Insulin = 1.0, BolusType = null };

        var entity = BolusMapper.ToEntity(model);

        entity.BolusType.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AllBolusTypeValues_MapCorrectly()
    {
        foreach (var bolusType in Enum.GetValues<BolusType>())
        {
            var model = new Bolus { Mills = 1700000000000, Insulin = 1.0, BolusType = bolusType };
            var entity = BolusMapper.ToEntity(model);
            entity.BolusType.Should().Be(bolusType.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AutomaticTrue_MapsCorrectly()
    {
        var model = new Bolus { Mills = 1700000000000, Insulin = 0.1, Automatic = true };

        var entity = BolusMapper.ToEntity(model);

        entity.Automatic.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullProgrammedAndDelivered_MapsToNull()
    {
        var model = new Bolus
        {
            Mills = 1700000000000,
            Insulin = 2.0,
            Programmed = null,
            Delivered = null
        };

        var entity = BolusMapper.ToEntity(model);

        entity.Programmed.Should().BeNull();
        entity.Delivered.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_SquareBolus_MapsDuration()
    {
        var model = new Bolus
        {
            Mills = 1700000000000,
            Insulin = 3.0,
            BolusType = BolusType.Square,
            Duration = 120
        };

        var entity = BolusMapper.ToEntity(model);

        entity.BolusType.Should().Be("Square");
        entity.Duration.Should().Be(120);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new BolusEntity
        {
            Id = id,
            Mills = 1700000000000,
            Insulin = 5.5,
            Programmed = 6.0,
            Delivered = 5.5,
            BolusType = "Normal",
            Automatic = false,
            Duration = 0,
            Device = "omnipod",
            App = "loop",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "bolus123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.Insulin.Should().Be(5.5);
        model.Programmed.Should().Be(6.0);
        model.Delivered.Should().Be(5.5);
        model.BolusType.Should().Be(BolusType.Normal);
        model.Automatic.Should().BeFalse();
        model.Duration.Should().Be(0);
        model.Device.Should().Be("omnipod");
        model.App.Should().Be("loop");
        model.UtcOffset.Should().Be(-300);
        model.DataSource.Should().Be("nightscout");
        model.CorrelationId.Should().Be(correlationId);
        model.LegacyId.Should().Be("bolus123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_InvalidBolusType_ReturnsNull()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Insulin = 1.0,
            BolusType = "InvalidType"
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.BolusType.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_NullBolusType_ReturnsNull()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Insulin = 1.0,
            BolusType = null
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.BolusType.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_AutomaticTrue_MapsCorrectly()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Insulin = 0.1,
            Automatic = true
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.Automatic.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_DualBolus_MapsCorrectly()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Insulin = 4.0,
            BolusType = "Dual",
            Duration = 60
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.BolusType.Should().Be(BolusType.Dual);
        model.Duration.Should().Be(60);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new BolusEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
            Insulin = 1.0
        };

        var model = new Bolus
        {
            Insulin = 5.0,
            Programmed = 5.5,
            Delivered = 5.0,
            BolusType = BolusType.Square,
            Automatic = true,
            Duration = 90,
            Mills = 1700000000000,
            Device = "tandem",
            App = "controliq",
            UtcOffset = 60,
            DataSource = "tidepool",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "upd456"
        };

        BolusMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Insulin.Should().Be(5.0);
        entity.Programmed.Should().Be(5.5);
        entity.Delivered.Should().Be(5.0);
        entity.BolusType.Should().Be("Square");
        entity.Automatic.Should().BeTrue();
        entity.Duration.Should().Be(90);
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
    public void UpdateEntity_SetsUpdatedAtTimestamp()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            SysCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var beforeUpdate = DateTime.UtcNow;

        var model = new Bolus { Insulin = 1.0 };
        BolusMapper.UpdateEntity(entity, model);

        entity.SysUpdatedAt.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsApsFields()
    {
        var model = new Bolus
        {
            Mills = 1700000000000,
            Insulin = 3.0,
            SyncIdentifier = "loop-sync-abc123",
            InsulinType = "Humalog",
            Unabsorbed = 1.5,
            IsBasalInsulin = true,
            PumpId = "pump-42",
            PumpSerial = "SN-12345",
            PumpType = "Omnipod DASH"
        };

        var entity = BolusMapper.ToEntity(model);

        entity.SyncIdentifier.Should().Be("loop-sync-abc123");
        entity.InsulinType.Should().Be("Humalog");
        entity.Unabsorbed.Should().Be(1.5);
        entity.IsBasalInsulin.Should().BeTrue();
        entity.PumpId.Should().Be("pump-42");
        entity.PumpSerial.Should().Be("SN-12345");
        entity.PumpType.Should().Be("Omnipod DASH");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsApsFields()
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Insulin = 3.0,
            SyncIdentifier = "loop-sync-abc123",
            InsulinType = "Humalog",
            Unabsorbed = 1.5,
            IsBasalInsulin = true,
            PumpId = "pump-42",
            PumpSerial = "SN-12345",
            PumpType = "Omnipod DASH"
        };

        var model = BolusMapper.ToDomainModel(entity);

        model.SyncIdentifier.Should().Be("loop-sync-abc123");
        model.InsulinType.Should().Be("Humalog");
        model.Unabsorbed.Should().Be(1.5);
        model.IsBasalInsulin.Should().BeTrue();
        model.PumpId.Should().Be("pump-42");
        model.PumpSerial.Should().Be("SN-12345");
        model.PumpType.Should().Be("Omnipod DASH");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var original = new Bolus
        {
            Id = id,
            Mills = 1700000000000,
            Insulin = 3.5,
            Programmed = 4.0,
            Delivered = 3.5,
            BolusType = BolusType.Dual,
            Automatic = true,
            Duration = 45,
            Device = "medtronic",
            App = "carelink",
            UtcOffset = -480,
            DataSource = "minimed",
            CorrelationId = correlationId,
            LegacyId = "rt789"
        };

        var entity = BolusMapper.ToEntity(original);
        var roundTripped = BolusMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.Mills.Should().Be(original.Mills);
        roundTripped.Insulin.Should().Be(original.Insulin);
        roundTripped.Programmed.Should().Be(original.Programmed);
        roundTripped.Delivered.Should().Be(original.Delivered);
        roundTripped.BolusType.Should().Be(original.BolusType);
        roundTripped.Automatic.Should().Be(original.Automatic);
        roundTripped.Duration.Should().Be(original.Duration);
        roundTripped.Device.Should().Be(original.Device);
        roundTripped.App.Should().Be(original.App);
        roundTripped.UtcOffset.Should().Be(original.UtcOffset);
        roundTripped.DataSource.Should().Be(original.DataSource);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);
        roundTripped.LegacyId.Should().Be(original.LegacyId);
    }
}
