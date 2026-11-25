using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Eversense.Mappers;
using Nocturne.Connectors.Eversense.Models;
using Xunit;

namespace Nocturne.Connectors.Eversense.Tests.Mappers;

/// <summary>
/// The stored legacy id of an Eversense reading, as an exact string.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class EversenseLegacyIdTests
{
    [Fact]
    public void SensorGlucose()
    {
        EversenseUserEvent[] events = [new() { EventTypeId = 1, Value = 110, EventDate = "2026-10-16T10:00:00" }];

        new EversenseSensorGlucoseMapper(NullLogger.Instance)
            .MapHistory(events)
            .Single().LegacyId.Should().Be("eversense_1792144800000");
    }
}
