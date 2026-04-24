using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Eversense.Mappers;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Eversense.Tests.Mappers;

public class EversenseSensorGlucoseMapperTests
{
    private readonly EversenseSensorGlucoseMapper _mapper = new(NullLogger.Instance);

    [Fact]
    public void Map_WithValidMgdlReading_ReturnsSensorGlucose()
    {
        var patient = CreatePatient(glucose: 120, trend: 3, units: 0, cgTime: "2026-04-24T12:00:00Z");

        var result = _mapper.Map(patient);

        result.Should().NotBeNull();
        result!.Mgdl.Should().Be(120);
        result.Direction.Should().Be(GlucoseDirection.Flat);
        result.DataSource.Should().Be(DataSources.EversenseConnector);
    }

    [Fact]
    public void Map_WithMmolReading_ConvertToMgdl()
    {
        var patient = CreatePatient(glucose: 6, trend: 3, units: 1, cgTime: "2026-04-24T12:00:00Z");

        var result = _mapper.Map(patient);

        result.Should().NotBeNull();
        result!.Mgdl.Should().BeApproximately(6 * 18.0182, 0.01);
    }

    [Theory]
    [InlineData(0, GlucoseDirection.None)]
    [InlineData(3, GlucoseDirection.Flat)]
    [InlineData(4, GlucoseDirection.FortyFiveUp)]
    [InlineData(5, GlucoseDirection.SingleUp)]
    [InlineData(7, GlucoseDirection.DoubleUp)]
    [InlineData(2, GlucoseDirection.FortyFiveDown)]
    [InlineData(1, GlucoseDirection.SingleDown)]
    [InlineData(6, GlucoseDirection.DoubleDown)]
    public void Map_TrendValues_MapsToCorrectDirection(int eversenseTrend, GlucoseDirection expected)
    {
        var patient = CreatePatient(glucose: 100, trend: eversenseTrend, cgTime: "2026-04-24T12:00:00Z");

        var result = _mapper.Map(patient);

        result.Should().NotBeNull();
        result!.Direction.Should().Be(expected);
    }

    [Fact]
    public void Map_WithUnknownTrend_DefaultsToNone()
    {
        var patient = CreatePatient(glucose: 100, trend: 99, cgTime: "2026-04-24T12:00:00Z");

        var result = _mapper.Map(patient);

        result.Should().NotBeNull();
        result!.Direction.Should().Be(GlucoseDirection.None);
    }

    [Fact]
    public void Map_WithInvalidTimestamp_ReturnsNull()
    {
        var patient = CreatePatient(glucose: 100, trend: 3, cgTime: "not-a-date");

        var result = _mapper.Map(patient);

        result.Should().BeNull();
    }

    [Fact]
    public void Map_SetsCorrectTimestampMills()
    {
        var patient = CreatePatient(glucose: 100, trend: 3, cgTime: "2026-04-24T12:00:00Z");

        var result = _mapper.Map(patient);

        result.Should().NotBeNull();
        var expectedMills = new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        result!.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(expectedMills).UtcDateTime);
    }

    private static EversensePatientDatum CreatePatient(
        int glucose = 100, int trend = 3, int units = 0, string cgTime = "2026-04-24T12:00:00Z")
    {
        return new EversensePatientDatum
        {
            CurrentGlucose = glucose,
            GlucoseTrend = trend,
            Units = units,
            CgTime = cgTime,
            IsTransmitterConnected = true,
            UserName = "test@example.com"
        };
    }
}
