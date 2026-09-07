using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoHeartRateMapper"/> — the SSV2 <c>validic/biometric_measurements</c> panel →
/// HeartRate, using the only HR-bearing field Glooko exposes (<c>restingHeartrate</c>). Records without a
/// resting heart rate (most of the panel) are skipped; soft-delete aware; guid-keyed deterministic ids.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoHeartRateMapperTests
{
    private readonly GlookoHeartRateMapper _mapper;

    public GlookoHeartRateMapperTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoHeartRateMapper("glooko-connector", timeMapper, logger);
    }

    [Fact]
    public void MapSsv2BiometricMeasurements_MapsRestingHeartRateToBpm()
    {
        var result = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement
        {
            RestingHeartrate = 58.4,
            Timestamp = "2024-03-01T07:00:00.000Z",
            Source = "fitbit",
            Guid = "hr-guid-1",
        }]);

        result.Should().ContainSingle();
        result[0].Bpm.Should().Be(58);
        result[0].DataSource.Should().Be("glooko-connector");
    }

    [Fact]
    public void MapSsv2BiometricMeasurements_SkipsRecordsWithNoHeartRate()
    {
        // A typical biometric panel record carrying other vitals but no resting HR.
        var result = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement
        {
            RestingHeartrate = null,
            Timestamp = "2024-03-01T07:00:00.000Z",
            Guid = "hr-guid-2",
        }]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2BiometricMeasurements_KeysIdOnGuid_Deterministically()
    {
        var a = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement { RestingHeartrate = 60, Timestamp = "2024-03-01T07:00:00.000Z", Guid = "g1" }]);
        var b = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement { RestingHeartrate = 70, Timestamp = "2025-03-01T07:00:00.000Z", Guid = "g1" }]);

        a[0].Id.Should().Be(b[0].Id);
        Guid.TryParse(a[0].Id, out _).Should().BeTrue("the Id must round-trip to the entity primary key for upsert");
    }

    [Fact]
    public void MapSsv2BiometricMeasurements_SkipsSoftDeleted()
    {
        var result = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement
        {
            RestingHeartrate = 65,
            Timestamp = "2024-03-01T07:00:00.000Z",
            Guid = "g1",
            SoftDeleted = true,
        }]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2BiometricMeasurements_AppliesTimestampCorrection()
    {
        var result = _mapper.MapSsv2BiometricMeasurements([new GlookoSsv2BiometricMeasurement
        {
            RestingHeartrate = 62,
            Timestamp = "2024-03-01T07:15:00.000Z",
            Guid = "g1",
        }]);

        result[0].Timestamp.Should().Be(new DateTime(2024, 3, 1, 7, 15, 0, DateTimeKind.Utc));
    }
}
