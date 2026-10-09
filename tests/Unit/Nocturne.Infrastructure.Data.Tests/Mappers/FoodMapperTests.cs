using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

public class FoodMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MalformedFoodsJson_YieldsNoFoods()
    {
        var entity = new FoodEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "Quick pick",
            Foods = """[{"name":""",
        };

        var model = FoodMapper.ToDomainModel(entity);

        model.Foods.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClientServerStampsAndIsValid_AreNotWrittenToTheRow()
    {
        var created = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        var updated = created.AddHours(1);
        var inbound = new Core.Models.Food
        {
            Name = "Oats", Type = "food", Carbs = 30,
            SrvCreated = 1, SrvModified = 2, IsValid = false,
        };

        var inserted = FoodMapper.ToEntity(inbound);
        var existing = new FoodEntity { Id = Guid.CreateVersion7(), Name = "Oats", SysCreatedAt = created, SysUpdatedAt = updated };
        FoodMapper.UpdateEntity(existing, inbound);

        inserted.DeletedAt.Should().BeNull();
        existing.DeletedAt.Should().BeNull();
        var served = FoodMapper.ToDomainModel(existing);
        served.SrvCreated.Should().Be(new DateTimeOffset(created).ToUnixTimeMilliseconds());
        served.SrvModified.Should().Be(new DateTimeOffset(updated).ToUnixTimeMilliseconds());
        served.IsValid.Should().BeNull();
    }
}
