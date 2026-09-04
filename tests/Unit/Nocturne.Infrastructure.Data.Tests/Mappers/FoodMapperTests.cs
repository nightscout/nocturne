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
}
