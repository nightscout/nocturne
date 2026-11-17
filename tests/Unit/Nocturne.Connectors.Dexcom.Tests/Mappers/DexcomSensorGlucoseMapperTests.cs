using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Dexcom.Mappers;
using Nocturne.Connectors.Dexcom.Models;
using Xunit;

namespace Nocturne.Connectors.Dexcom.Tests.Mappers;

public class DexcomSensorGlucoseMapperTests
{
    /// <summary>
    /// A null in Share's array is one malformed record. It is dropped before mapping, so the readings
    /// beside it still publish rather than the run failing on every cycle.
    /// </summary>
    [Fact]
    public void MapBatchData_NullEntry_MapsTheOthers()
    {
        DexcomEntry[] entries =
        [
            null!,
            new() { Value = 120, Wt = "Date(1781956800000)" },
            null!,
        ];

        var readings = new DexcomSensorGlucoseMapper(NullLogger.Instance).MapBatchData(entries, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        readings.Should().ContainSingle().Which.Mgdl.Should().Be(120);
    }
}
