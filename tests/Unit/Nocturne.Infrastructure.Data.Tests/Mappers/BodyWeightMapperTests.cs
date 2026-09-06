using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

public class BodyWeightMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var model = new BodyWeight
        {
            Id = Guid.CreateVersion7().ToString(),
            Mills = 1700000000000,
            WeightKg = 75.5m,
            BodyFatPercent = 18.2m,
            LeanMassKg = 61.7m,
            Device = "withings",
            EnteredBy = "user@example.com",
            CreatedAt = "2024-11-14T22:13:20.000Z",
            UtcOffset = -300,
        };

        var entity = BodyWeightMapper.ToEntity(model);

        entity.Mills.Should().Be(1700000000000);
        entity.WeightKg.Should().Be(75.5m);
        entity.BodyFatPercent.Should().Be(18.2m);
        entity.LeanMassKg.Should().Be(61.7m);
        entity.Device.Should().Be("withings");
        entity.EnteredBy.Should().Be("user@example.com");
        entity.CreatedAt.Should().Be("2024-11-14T22:13:20.000Z");
        entity.UtcOffset.Should().Be(-300);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_NullOrEmptyId_GeneratesUuidV7()
    {
        var model = new BodyWeight { Mills = 1700000000000, WeightKg = 80m };

        var entity = BodyWeightMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
        entity.Id.Version.Should().Be(7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyStringId_GeneratesUuidV7()
    {
        var model = new BodyWeight { Id = "", Mills = 1700000000000, WeightKg = 80m };

        var entity = BodyWeightMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_ValidMongoId_PreservesOriginalId()
    {
        var mongoId = "507f1f77bcf86cd799439011";
        var model = new BodyWeight
        {
            Id = mongoId,
            Mills = 1700000000000,
            WeightKg = 70m,
        };

        var entity = BodyWeightMapper.ToEntity(model);

        entity.OriginalId.Should().Be(mongoId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_GuidId_DoesNotSetOriginalId()
    {
        var guidId = Guid.CreateVersion7().ToString();
        var model = new BodyWeight
        {
            Id = guidId,
            Mills = 1700000000000,
            WeightKg = 70m,
        };

        var entity = BodyWeightMapper.ToEntity(model);

        entity.OriginalId.Should().BeNull();
        entity.Id.Should().Be(Guid.Parse(guidId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var entity = new BodyWeightEntity
        {
            Id = id,
            Mills = 1700000000000,
            WeightKg = 75.5m,
            BodyFatPercent = 18.2m,
            LeanMassKg = 61.7m,
            Device = "withings",
            EnteredBy = "user@example.com",
            CreatedAt = "2024-11-14T22:13:20.000Z",
            UtcOffset = -300,
        };

        var model = BodyWeightMapper.ToDomainModel(entity);

        model.Id.Should().Be(id.ToString());
        model.Mills.Should().Be(1700000000000);
        model.WeightKg.Should().Be(75.5m);
        model.BodyFatPercent.Should().Be(18.2m);
        model.LeanMassKg.Should().Be(61.7m);
        model.Device.Should().Be("withings");
        model.EnteredBy.Should().Be("user@example.com");
        model.CreatedAt.Should().Be("2024-11-14T22:13:20.000Z");
        model.UtcOffset.Should().Be(-300);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_WithOriginalId_UsesOriginalId()
    {
        var mongoId = "507f1f77bcf86cd799439011";
        var entity = new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(),
            OriginalId = mongoId,
            Mills = 1700000000000,
            WeightKg = 70m,
        };

        var model = BodyWeightMapper.ToDomainModel(entity);

        model.Id.Should().Be(mongoId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_WithoutOriginalId_FallsBackToGuidString()
    {
        var id = Guid.CreateVersion7();
        var entity = new BodyWeightEntity
        {
            Id = id,
            OriginalId = null,
            Mills = 1700000000000,
            WeightKg = 70m,
        };

        var model = BodyWeightMapper.ToDomainModel(entity);

        model.Id.Should().Be(id.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFields()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new BodyWeightEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Mills = 1000,
            WeightKg = 70m,
        };

        var model = new BodyWeight
        {
            Mills = 1700000000000,
            WeightKg = 75.5m,
            BodyFatPercent = 18.2m,
            LeanMassKg = 61.7m,
            Device = "garmin",
            EnteredBy = "admin",
            CreatedAt = "2024-11-14T22:13:20.000Z",
            UtcOffset = 60,
        };

        BodyWeightMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId, "Id should not change on update");
        entity.SysCreatedAt.Should().Be(originalCreatedAt, "SysCreatedAt should not change on update");
        entity.Mills.Should().Be(1700000000000);
        entity.WeightKg.Should().Be(75.5m);
        entity.BodyFatPercent.Should().Be(18.2m);
        entity.LeanMassKg.Should().Be(61.7m);
        entity.Device.Should().Be("garmin");
        entity.EnteredBy.Should().Be("admin");
        entity.CreatedAt.Should().Be("2024-11-14T22:13:20.000Z");
        entity.UtcOffset.Should().Be(60);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_DoesNotTouchSysUpdatedAt()
    {
        var stale = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(),
            SysCreatedAt = stale,
            SysUpdatedAt = stale,
        };

        var model = new BodyWeight { WeightKg = 80m };
        BodyWeightMapper.UpdateEntity(entity, model);

        // NocturneDbContext.UpdateTimestamps stamps sys_updated_at, and only for rows with a
        // real modification; a mapper stamp would force an UPDATE on no-op upserts.
        entity.SysUpdatedAt.Should().Be(stale);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_PreservesAllFields()
    {
        var id = Guid.CreateVersion7();
        var original = new BodyWeight
        {
            Id = id.ToString(),
            Mills = 1700000000000,
            WeightKg = 75.5m,
            BodyFatPercent = 18.2m,
            LeanMassKg = 61.7m,
            Device = "withings",
            EnteredBy = "user@example.com",
            CreatedAt = "2024-11-14T22:13:20.000Z",
            UtcOffset = -300,
        };

        var entity = BodyWeightMapper.ToEntity(original);
        var roundTripped = BodyWeightMapper.ToDomainModel(entity);

        roundTripped.Id.Should().Be(original.Id);
        roundTripped.Mills.Should().Be(original.Mills);
        roundTripped.WeightKg.Should().Be(original.WeightKg);
        roundTripped.BodyFatPercent.Should().Be(original.BodyFatPercent);
        roundTripped.LeanMassKg.Should().Be(original.LeanMassKg);
        roundTripped.Device.Should().Be(original.Device);
        roundTripped.EnteredBy.Should().Be(original.EnteredBy);
        roundTripped.CreatedAt.Should().Be(original.CreatedAt);
        roundTripped.UtcOffset.Should().Be(original.UtcOffset);
    }
}
