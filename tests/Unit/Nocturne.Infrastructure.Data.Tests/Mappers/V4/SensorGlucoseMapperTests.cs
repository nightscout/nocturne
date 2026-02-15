using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class SensorGlucoseMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var model = new SensorGlucose
        {
            Id = id,
            Mills = 1700000000000,
            Mgdl = 120,
            Mmol = 6.7,
            Direction = GlucoseDirection.Flat,
            Trend = GlucoseTrend.Flat,
            TrendRate = 0.5,
            Noise = 1,
            Device = "dexcom",
            App = "xdrip",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "abc123"
        };

        var entity = SensorGlucoseMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.Mgdl.Should().Be(120);
        entity.Mmol.Should().Be(6.7);
        entity.Direction.Should().Be("Flat");
        entity.Trend.Should().Be(4); // GlucoseTrend.Flat = 4
        entity.TrendRate.Should().Be(0.5);
        entity.Noise.Should().Be(1);
        entity.Device.Should().Be("dexcom");
        entity.App.Should().Be("xdrip");
        entity.UtcOffset.Should().Be(-300);
        entity.DataSource.Should().Be("nightscout");
        entity.CorrelationId.Should().Be(correlationId);
        entity.LegacyId.Should().Be("abc123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new SensorGlucose { Mills = 1700000000000, Mgdl = 100 };

        var entity = SensorGlucoseMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullDirection_MapsToNull()
    {
        var model = new SensorGlucose { Mills = 1700000000000, Mgdl = 100, Direction = null };

        var entity = SensorGlucoseMapper.ToEntity(model);

        entity.Direction.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullTrend_MapsToNull()
    {
        var model = new SensorGlucose { Mills = 1700000000000, Mgdl = 100, Trend = null };

        var entity = SensorGlucoseMapper.ToEntity(model);

        entity.Trend.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AllDirectionValues_MapCorrectly()
    {
        var directions = Enum.GetValues<GlucoseDirection>();
        foreach (var direction in directions)
        {
            var model = new SensorGlucose { Mills = 1700000000000, Mgdl = 100, Direction = direction };
            var entity = SensorGlucoseMapper.ToEntity(model);
            entity.Direction.Should().Be(direction.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AllTrendValues_MapCorrectly()
    {
        var trends = Enum.GetValues<GlucoseTrend>();
        foreach (var trend in trends)
        {
            var model = new SensorGlucose { Mills = 1700000000000, Mgdl = 100, Trend = trend };
            var entity = SensorGlucoseMapper.ToEntity(model);
            entity.Trend.Should().Be((int)trend);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new SensorGlucoseEntity
        {
            Id = id,
            Mills = 1700000000000,
            Mgdl = 120,
            Mmol = 6.7,
            Direction = "Flat",
            Trend = 4,
            TrendRate = 0.5,
            Noise = 1,
            Device = "dexcom",
            App = "xdrip",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "abc123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt
        };

        var model = SensorGlucoseMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.Mgdl.Should().Be(120);
        model.Mmol.Should().Be(6.7);
        model.Direction.Should().Be(GlucoseDirection.Flat);
        model.Trend.Should().Be(GlucoseTrend.Flat);
        model.TrendRate.Should().Be(0.5);
        model.Noise.Should().Be(1);
        model.Device.Should().Be("dexcom");
        model.App.Should().Be("xdrip");
        model.UtcOffset.Should().Be(-300);
        model.DataSource.Should().Be("nightscout");
        model.CorrelationId.Should().Be(correlationId);
        model.LegacyId.Should().Be("abc123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_InvalidDirection_ReturnsNull()
    {
        var entity = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Direction = "InvalidValue"
        };

        var model = SensorGlucoseMapper.ToDomainModel(entity);

        model.Direction.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_NullDirection_ReturnsNull()
    {
        var entity = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Direction = null
        };

        var model = SensorGlucoseMapper.ToDomainModel(entity);

        model.Direction.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_NullTrend_ReturnsNull()
    {
        var entity = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            Trend = null
        };

        var model = SensorGlucoseMapper.ToDomainModel(entity);

        model.Trend.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_TrendRoundTrips()
    {
        foreach (var trend in Enum.GetValues<GlucoseTrend>())
        {
            var entity = new SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(),
                Mills = 1700000000000,
                Trend = (int)trend
            };

            var model = SensorGlucoseMapper.ToDomainModel(entity);

            model.Trend.Should().Be(trend);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new SensorGlucoseEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
            Mgdl = 80
        };

        var model = new SensorGlucose
        {
            Mgdl = 150,
            Mmol = 8.3,
            Direction = GlucoseDirection.SingleUp,
            Trend = GlucoseTrend.SingleUp,
            TrendRate = 2.0,
            Noise = 2,
            Mills = 1700000000000,
            Device = "libre",
            App = "librelink",
            UtcOffset = 60,
            DataSource = "glooko",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "xyz789"
        };

        SensorGlucoseMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Mgdl.Should().Be(150);
        entity.Mmol.Should().Be(8.3);
        entity.Direction.Should().Be("SingleUp");
        entity.Trend.Should().Be(2); // GlucoseTrend.SingleUp = 2
        entity.TrendRate.Should().Be(2.0);
        entity.Noise.Should().Be(2);
        entity.Mills.Should().Be(1700000000000);
        entity.Device.Should().Be("libre");
        entity.App.Should().Be("librelink");
        entity.UtcOffset.Should().Be(60);
        entity.DataSource.Should().Be("glooko");
        entity.CorrelationId.Should().Be(model.CorrelationId);
        entity.LegacyId.Should().Be("xyz789");
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_SetsUpdatedAtTimestamp()
    {
        var entity = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            SysCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var beforeUpdate = DateTime.UtcNow;

        var model = new SensorGlucose { Mgdl = 100 };
        SensorGlucoseMapper.UpdateEntity(entity, model);

        entity.SysUpdatedAt.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var correlationId = Guid.NewGuid();
        var original = new SensorGlucose
        {
            Id = id,
            Mills = 1700000000000,
            Mgdl = 120,
            Mmol = 6.7,
            Direction = GlucoseDirection.FortyFiveDown,
            Trend = GlucoseTrend.FortyFiveDown,
            TrendRate = -0.8,
            Noise = 3,
            Device = "dexcom",
            App = "xdrip",
            UtcOffset = -300,
            DataSource = "nightscout",
            CorrelationId = correlationId,
            LegacyId = "abc123"
        };

        var entity = SensorGlucoseMapper.ToEntity(original);
        var roundTripped = SensorGlucoseMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.Mills.Should().Be(original.Mills);
        roundTripped.Mgdl.Should().Be(original.Mgdl);
        roundTripped.Mmol.Should().Be(original.Mmol);
        roundTripped.Direction.Should().Be(original.Direction);
        roundTripped.Trend.Should().Be(original.Trend);
        roundTripped.TrendRate.Should().Be(original.TrendRate);
        roundTripped.Noise.Should().Be(original.Noise);
        roundTripped.Device.Should().Be(original.Device);
        roundTripped.App.Should().Be(original.App);
        roundTripped.UtcOffset.Should().Be(original.UtcOffset);
        roundTripped.DataSource.Should().Be(original.DataSource);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);
        roundTripped.LegacyId.Should().Be(original.LegacyId);
    }
}
