using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Performance.Tests.Shared;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class CarbIntakeEntityFactory
{
    public static List<CarbIntakeEntity> Generate(int count)
        => DemoFixtureGenerator.GenerateCarbIntakes(
                count, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Select(CarbIntakeMapper.ToEntity)
            .ToList();
}
