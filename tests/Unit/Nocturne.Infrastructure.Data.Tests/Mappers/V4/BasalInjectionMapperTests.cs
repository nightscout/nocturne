using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class BasalInjectionMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MalformedInsulinContextJson_YieldsNoInsulinContext()
    {
        var entity = new BasalInjectionEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Units = 12,
            InsulinContextJson = """{"insulinName":""",
        };

        var model = BasalInjectionMapper.ToDomainModel(entity);

        model.InsulinContext.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }
}
