using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Dexcom.Mappers;
using Nocturne.Connectors.Dexcom.Models;
using Xunit;

namespace Nocturne.Connectors.Dexcom.Tests.Mappers;

/// <summary>
/// The stored legacy id of a Dexcom reading, as an exact string.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class DexcomLegacyIdTests
{
    [Fact]
    public void SensorGlucose()
    {
        DexcomEntry[] entries = [new() { Value = 120, Wt = "Date(1781956800000)" }];

        new DexcomSensorGlucoseMapper(NullLogger.Instance)
            .MapBatchData(entries)
            .Single().LegacyId.Should().Be("dexcom_Date(1781956800000)");
    }
}
