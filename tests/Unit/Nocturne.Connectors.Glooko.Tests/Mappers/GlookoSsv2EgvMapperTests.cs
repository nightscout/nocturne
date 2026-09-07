using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Verifies the SSV2 egvs → SensorGlucose mapping: mg/dL × 100 decoding, skip rules for
/// calculated/soft-deleted/non-positive readings, stable guid-keyed SyncIdentifier, and fake-UTC
/// timezone correction through the shared time mapper.
/// </summary>
public class GlookoSsv2EgvMapperTests
{
    private const string ConnectorSource = "glooko_test";

    private static GlookoSensorGlucoseMapper Mapper(TimezoneTimeline? timeline = null, double offset = 0)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = offset };
        var timeMapper = new GlookoTimeMapper(config, NullLogger.Instance);
        if (timeline is not null)
            timeMapper.UseTimeline(timeline);
        return new GlookoSensorGlucoseMapper(config, ConnectorSource, timeMapper, NullLogger.Instance);
    }

    private static GlookoEgv Egv(double glucoseValueX100, string displayTime, string? guid = "g1",
        bool calculated = false, bool softDeleted = false, string? trend = null) =>
        new()
        {
            GlucoseValue = glucoseValueX100,
            DisplayTime = displayTime,
            Guid = guid,
            Calculated = calculated,
            SoftDeleted = softDeleted,
            TrendArrow = trend,
        };

    [Fact]
    public void Egvs_DecodesMgdlAndKeysOnGuid()
    {
        var result = Mapper().TransformEgvsToSensorGlucose([Egv(17601, "2026-03-18T00:03:12.000Z", "abc")]).Single();

        result.Mgdl.Should().Be(176.01);
        result.SyncIdentifier.Should().Be("glooko_egv_abc");
        result.LegacyId.Should().Be("glooko_egv_abc");
        result.DataSource.Should().Be(ConnectorSource);
    }

    [Fact]
    public void Egvs_SkipsCalculatedSoftDeletedAndNonPositive()
    {
        var input = new[]
        {
            Egv(10000, "2026-03-18T00:00:00.000Z", "ok"),
            Egv(12000, "2026-03-18T00:05:00.000Z", "calc", calculated: true),
            Egv(13000, "2026-03-18T00:10:00.000Z", "del", softDeleted: true),
            Egv(0, "2026-03-18T00:15:00.000Z", "zero"),
        };

        var result = Mapper().TransformEgvsToSensorGlucose(input).ToList();

        result.Should().ContainSingle().Which.SyncIdentifier.Should().Be("glooko_egv_ok");
    }

    [Fact]
    public void Egvs_NullCollection_ReturnsEmpty()
    {
        Mapper().TransformEgvsToSensorGlucose(null).Should().BeEmpty();
    }

    [Fact]
    public void Egvs_WithoutGuid_FallsBackToRawDisplayTimeKey()
    {
        var result = Mapper().TransformEgvsToSensorGlucose([Egv(9000, "2026-03-18T00:03:12.000Z", guid: null)]).Single();

        result.SyncIdentifier.Should().Be("glooko_egv_raw_2026-03-18T00:03:12.000Z");
    }

    [Fact]
    public void Egvs_WithSydneyTimeline_CorrectsFakeUtcUsingDstOffset()
    {
        var timeline = new TimezoneTimeline(
        [
            new TimezoneTimelineEntry { Timezone = "Australia/Sydney", EffectiveFrom = DateTime.MinValue },
        ]);

        // Fake-UTC midnight 2026-01-10 = local midnight Sydney (AEDT +11) -> 2026-01-09 13:00Z.
        var result = Mapper(timeline).TransformEgvsToSensorGlucose([Egv(12000, "2026-01-10T00:00:00.000Z", "tz")]).Single();

        result.Timestamp.Should().Be(new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc));
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Egvs_MapsTrendArrowToDirection()
    {
        var result = Mapper().TransformEgvsToSensorGlucose([Egv(10000, "2026-03-18T00:03:12.000Z", "t", trend: "SingleUp")]).Single();

        result.Direction.Should().Be(GlucoseDirection.SingleUp);
    }
}
