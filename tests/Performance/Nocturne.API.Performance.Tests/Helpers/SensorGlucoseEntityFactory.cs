using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Performance.Tests.Shared;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class SensorGlucoseEntityFactory
{
    public static List<SensorGlucoseEntity> Generate(int count)
        => DemoFixtureGenerator.GenerateSensorGlucose(
                count, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Select(SensorGlucoseMapper.ToEntity)
            .ToList();
}
