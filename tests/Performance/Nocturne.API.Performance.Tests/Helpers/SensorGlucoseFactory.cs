using Nocturne.Performance.Tests.Shared;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class SensorGlucoseFactory
{
    public static List<SensorGlucose> Generate(int count)
        => DemoFixtureGenerator.GenerateSensorGlucose(
            count, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToList();
}
