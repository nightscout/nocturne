using FluentAssertions;
using Nocturne.Connectors.FreeStyle.Mappers;
using Nocturne.Connectors.FreeStyle.Models;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Mappers;

/// <summary>
/// The stored keys of every LibreLinkUp record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class LibreLegacyIdTests
{
    [Fact]
    public void SensorGlucose()
    {
        var measurement = new LibreGlucoseMeasurement
        {
            FactoryTimestamp = "1/15/2024 2:30:00 PM", ValueInMgPerDl = 120, TrendArrow = 3,
        };

        new LibreSensorGlucoseMapper().ConvertMeasurement(measurement)!
            .LegacyId.Should().Be("libre_1/15/2024 2:30:00 PM");
    }

    [Fact]
    public void SensorStart()
    {
        var sensor = new LibreSensor { SerialNumber = "0M0001", ActivatedAt = 1_700_000_000 };

        LibreSensorSessionMapper.ToSensorStart(sensor)!
            .SyncIdentifier.Should().Be("libre_sensor_0M0001");
    }
}
