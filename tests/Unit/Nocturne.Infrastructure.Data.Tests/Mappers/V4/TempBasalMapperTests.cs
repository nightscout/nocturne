using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class TempBasalMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var pumpDeviceId = Guid.NewGuid();
        var model = new TempBasal
        {
            Id = id,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime,
            UtcOffset = -300,
            Device = "omnipod",
            App = "loop",
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "tb123",
            Rate = 1.25,
            ScheduledRate = 0.85,
            Origin = TempBasalOrigin.Algorithm,
            DeviceId = pumpDeviceId,
            PumpRecordId = "pump-rec-001"
        };

        var entity = TempBasalMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime);
        entity.UtcOffset.Should().Be(-300);
        entity.Device.Should().Be("omnipod");
        entity.App.Should().Be("loop");
        entity.DataSource.Should().Be("nightscout");
        entity.CorrelationId.Should().Be(correlationId);
        entity.LegacyId.Should().Be("tb123");
        entity.Rate.Should().Be(1.25);
        entity.ScheduledRate.Should().Be(0.85);
        entity.Origin.Should().Be("Algorithm");
        entity.DeviceId.Should().Be(pumpDeviceId);
        entity.PumpRecordId.Should().Be("pump-rec-001");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new TempBasal { StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, Rate = 1.0 };

        var entity = TempBasalMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullEndMills_MapsToNull()
    {
        var model = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 1.0,
            EndTimestamp = null
        };

        var entity = TempBasalMapper.ToEntity(model);

        entity.EndTimestamp.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AllOriginValues_MapCorrectly()
    {
        foreach (var origin in Enum.GetValues<TempBasalOrigin>())
        {
            var model = new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
                Rate = 1.0,
                Origin = origin
            };
            var entity = TempBasalMapper.ToEntity(model);
            entity.Origin.Should().Be(origin.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_SuspendedOrigin_MapsCorrectly()
    {
        var model = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 0.0,
            Origin = TempBasalOrigin.Suspended
        };

        var entity = TempBasalMapper.ToEntity(model);

        entity.Origin.Should().Be("Suspended");
        entity.Rate.Should().Be(0.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullOptionalFields_MapsToNull()
    {
        var model = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 1.0,
            EndTimestamp = null,
            ScheduledRate = null,
            DeviceId = null,
            PumpRecordId = null
        };

        var entity = TempBasalMapper.ToEntity(model);

        entity.EndTimestamp.Should().BeNull();
        entity.ScheduledRate.Should().BeNull();
        entity.DeviceId.Should().BeNull();
        entity.PumpRecordId.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var pumpDeviceId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new TempBasalEntity
        {
            Id = id,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime,
            UtcOffset = -300,
            Device = "omnipod",
            App = "loop",
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "tb123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt,
            Rate = 1.25,
            ScheduledRate = 0.85,
            Origin = "Algorithm",
            DeviceId = pumpDeviceId,
            PumpRecordId = "pump-rec-001"
        };

        var model = TempBasalMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.StartMills.Should().Be(1700000000000);
        model.EndMills.Should().Be(1700001800000);
        model.UtcOffset.Should().Be(-300);
        model.Device.Should().Be("omnipod");
        model.App.Should().Be("loop");
        model.DataSource.Should().Be("nightscout");
        model.CorrelationId.Should().Be(correlationId);
        model.LegacyId.Should().Be("tb123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
        model.Rate.Should().Be(1.25);
        model.ScheduledRate.Should().Be(0.85);
        model.Origin.Should().Be(TempBasalOrigin.Algorithm);
        model.DeviceId.Should().Be(pumpDeviceId);
        model.PumpRecordId.Should().Be("pump-rec-001");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_InvalidOrigin_DefaultsToInferred()
    {
        var entity = new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 1.0,
            Origin = "InvalidOrigin"
        };

        var model = TempBasalMapper.ToDomainModel(entity);

        model.Origin.Should().Be(TempBasalOrigin.Inferred);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_NullEndMills_ReturnsNull()
    {
        var entity = new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 1.0,
            Origin = "Manual",
            EndTimestamp = null
        };

        var model = TempBasalMapper.ToDomainModel(entity);

        model.EndMills.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_AllOriginValues_ParseCorrectly()
    {
        foreach (var origin in Enum.GetValues<TempBasalOrigin>())
        {
            var entity = new TempBasalEntity
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
                Rate = 1.0,
                Origin = origin.ToString()
            };

            var model = TempBasalMapper.ToDomainModel(entity);

            model.Origin.Should().Be(origin);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new TempBasalEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
            Rate = 0.5,
            Origin = "Manual"
        };

        var newCorrelationId = Guid.NewGuid();
        var newDeviceId = Guid.NewGuid();
        var model = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime,
            UtcOffset = 60,
            Device = "tandem",
            App = "controliq",
            DataSource = "tidepool",
            CorrelationId = newCorrelationId,
            LegacyId = "upd456",
            Rate = 2.5,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
            DeviceId = newDeviceId,
            PumpRecordId = "pump-rec-002"
        };

        TempBasalMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime);
        entity.UtcOffset.Should().Be(60);
        entity.Device.Should().Be("tandem");
        entity.App.Should().Be("controliq");
        entity.DataSource.Should().Be("tidepool");
        entity.CorrelationId.Should().Be(newCorrelationId);
        entity.LegacyId.Should().Be("upd456");
        entity.Rate.Should().Be(2.5);
        entity.ScheduledRate.Should().Be(1.0);
        entity.Origin.Should().Be("Algorithm");
        entity.DeviceId.Should().Be(newDeviceId);
        entity.PumpRecordId.Should().Be("pump-rec-002");
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_SetsUpdatedAtTimestamp()
    {
        var entity = new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            SysCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Origin = "Manual"
        };
        var beforeUpdate = DateTime.UtcNow;

        var model = new TempBasal { StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, Rate = 1.0 };
        TempBasalMapper.UpdateEntity(entity, model);

        entity.SysUpdatedAt.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var pumpDeviceId = Guid.NewGuid();
        var original = new TempBasal
        {
            Id = id,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime,
            UtcOffset = -480,
            Device = "medtronic",
            App = "carelink",
            DataSource = "minimed",
            CorrelationId = correlationId,
            LegacyId = "rt789",
            Rate = 0.75,
            ScheduledRate = 1.2,
            Origin = TempBasalOrigin.Scheduled,
            DeviceId = pumpDeviceId,
            PumpRecordId = "pump-rec-003"
        };

        var entity = TempBasalMapper.ToEntity(original);
        var roundTripped = TempBasalMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.StartMills.Should().Be(original.StartMills);
        roundTripped.EndMills.Should().Be(original.EndMills);
        roundTripped.UtcOffset.Should().Be(original.UtcOffset);
        roundTripped.Device.Should().Be(original.Device);
        roundTripped.App.Should().Be(original.App);
        roundTripped.DataSource.Should().Be(original.DataSource);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);
        roundTripped.LegacyId.Should().Be(original.LegacyId);
        roundTripped.Rate.Should().Be(original.Rate);
        roundTripped.ScheduledRate.Should().Be(original.ScheduledRate);
        roundTripped.Origin.Should().Be(original.Origin);
        roundTripped.DeviceId.Should().Be(original.DeviceId);
        roundTripped.PumpRecordId.Should().Be(original.PumpRecordId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_WithInsulinContext_PreservesAllFields()
    {
        var insulinId = Guid.NewGuid();
        var original = new TempBasal
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 0.8,
            Origin = TempBasalOrigin.Algorithm,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = insulinId,
                InsulinName = "Humalog",
                Dia = 4.0,
                Peak = 75,
                Curve = "rapid-acting",
                Concentration = 100,
            },
        };

        var entity = TempBasalMapper.ToEntity(original);
        entity.InsulinContextJson.Should().NotBeNullOrEmpty();

        var roundTripped = TempBasalMapper.ToDomainModel(entity);

        roundTripped.InsulinContext.Should().NotBeNull();
        roundTripped.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        roundTripped.InsulinContext.InsulinName.Should().Be("Humalog");
        roundTripped.InsulinContext.Dia.Should().Be(4.0);
        roundTripped.InsulinContext.Peak.Should().Be(75);
        roundTripped.InsulinContext.Curve.Should().Be("rapid-acting");
        roundTripped.InsulinContext.Concentration.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_WithNullInsulinContext_StaysNull()
    {
        var model = new TempBasal
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Rate = 1.0,
            Origin = TempBasalOrigin.Manual,
        };

        var entity = TempBasalMapper.ToEntity(model);
        entity.InsulinContextJson.Should().BeNull();

        var roundTripped = TempBasalMapper.ToDomainModel(entity);
        roundTripped.InsulinContext.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_AllOriginValues_PreserveCorrectly()
    {
        foreach (var origin in Enum.GetValues<TempBasalOrigin>())
        {
            var original = new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
                Rate = 1.0,
                Origin = origin
            };

            var entity = TempBasalMapper.ToEntity(original);
            var roundTripped = TempBasalMapper.ToDomainModel(entity);

            roundTripped.Origin.Should().Be(origin,
                because: $"TempBasalOrigin.{origin} should survive a round trip");
        }
    }
}
