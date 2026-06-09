using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Verifies the Glooko sensor-glucose mapper converts fake-UTC timestamps via the timezone timeline
/// (DST-aware) and stamps a stable SyncIdentifier so re-correction upserts in place.
/// </summary>
public class GlookoSensorGlucoseTimelineTests
{
    private const string ConnectorSource = "glooko_test";

    private static long Unix(DateTime fakeUtcWallClock) =>
        new DateTimeOffset(DateTime.SpecifyKind(fakeUtcWallClock, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static GlookoSensorGlucoseMapper MapperWithTimeline(TimezoneTimeline? timeline, double offset = 0)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = offset };
        var timeMapper = new GlookoTimeMapper(config, NullLogger.Instance);
        if (timeline is not null)
            timeMapper.UseTimeline(timeline);
        return new GlookoSensorGlucoseMapper(config, ConnectorSource, timeMapper, NullLogger.Instance);
    }

    private static GlookoV3GraphResponse CgmResponse(long x, double y) =>
        new()
        {
            Series = new GlookoV3Series
            {
                CgmNormal = [new GlookoV3GlucoseDataPoint { X = x, Y = y, Value = (long)(y * 100), Calculated = false }],
            },
        };

    [Fact]
    public void V3Cgm_WithSydneyTimeline_ConvertsUsingDstOffsetForTheDate()
    {
        var timeline = new TimezoneTimeline(
        [
            new TimezoneTimelineEntry { Timezone = "Australia/Sydney", EffectiveFrom = DateTime.MinValue },
        ]);
        var mapper = MapperWithTimeline(timeline);

        // Fake-UTC midnight on 2026-01-10 = local midnight Sydney (AEDT +11) -> 2026-01-09 13:00Z.
        var x = Unix(new DateTime(2026, 1, 10, 0, 0, 0));
        var result = mapper.TransformV3ToSensorGlucose(CgmResponse(x, 120), meterUnits: null).Single();

        result.Timestamp.Should().Be(new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc));
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        result.SyncIdentifier.Should().Be($"glooko_v3_{x}");
    }

    [Fact]
    public void V3Cgm_NoTimeline_FallsBackToStaticOffset()
    {
        var mapper = MapperWithTimeline(timeline: null, offset: 0);

        var x = Unix(new DateTime(2026, 1, 10, 0, 0, 0));
        var result = mapper.TransformV3ToSensorGlucose(CgmResponse(x, 120), meterUnits: null).Single();

        // Offset 0, no timeline: the fake-UTC value is taken as-is.
        result.Timestamp.Should().Be(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void V2Cgm_WithTimeline_ConvertsAndUsesStableGuidSyncIdentifier()
    {
        var timeline = new TimezoneTimeline(
        [
            new TimezoneTimelineEntry { Timezone = "America/Toronto", EffectiveFrom = DateTime.MinValue },
        ]);
        var mapper = MapperWithTimeline(timeline);

        var batch = new GlookoBatchData
        {
            Readings =
            [
                new GlookoCgmReading { Timestamp = "2026-03-09T07:00:00.000Z", Value = 12000, Guid = "abc-123" },
            ],
        };

        var result = mapper.TransformBatchDataToSensorGlucose(batch).Single();

        // 07:00 local Toronto on Mar 9 (EDT -4) -> 11:00Z.
        result.Timestamp.Should().Be(new DateTime(2026, 3, 9, 11, 0, 0, DateTimeKind.Utc));
        result.SyncIdentifier.Should().Be("glooko_cgm_abc-123");
    }

    [Fact]
    public void V2Cgm_NoGuid_UsesRawTimestampSyncIdentifier_StableAcrossCorrection()
    {
        var mapper = MapperWithTimeline(
            new TimezoneTimeline([new TimezoneTimelineEntry { Timezone = "America/Toronto", EffectiveFrom = DateTime.MinValue }]));

        var batch = new GlookoBatchData
        {
            Readings = [new GlookoCgmReading { Timestamp = "2026-03-09T07:00:00.000Z", Value = 12000 }],
        };

        var result = mapper.TransformBatchDataToSensorGlucose(batch).Single();

        // Keyed on the RAW fake-UTC string, not the corrected timestamp.
        result.SyncIdentifier.Should().Be("glooko_cgm_raw_2026-03-09T07:00:00.000Z");
    }
}
