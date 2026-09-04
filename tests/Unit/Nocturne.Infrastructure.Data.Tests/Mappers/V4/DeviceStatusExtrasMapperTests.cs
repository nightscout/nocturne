using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class DeviceStatusExtrasMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MalformedExtrasJson_YieldsNoExtras()
    {
        var entity = new DeviceStatusExtrasEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            ExtrasJson = """{"unterminated":""",
        };

        var model = DeviceStatusExtrasMapper.ToDomainModel(entity);

        model.Extras.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }
}
