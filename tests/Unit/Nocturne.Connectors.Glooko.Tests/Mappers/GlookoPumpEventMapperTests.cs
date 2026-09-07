using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Verifies the SSV2 pumps/events → DeviceEvent mapping: known types map to the strongly-typed enum,
/// unrecognised/soft-deleted events are dropped, timestamps are fake-UTC corrected, and the
/// SyncIdentifier is guid-stable for upserts.
/// </summary>
public class GlookoPumpEventMapperTests
{
    private const string ConnectorSource = "glooko_test";

    private static GlookoPumpEventMapper Mapper(TimezoneTimeline? timeline = null, double offset = 0)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = offset };
        var timeMapper = new GlookoTimeMapper(config, NullLogger.Instance);
        if (timeline is not null)
            timeMapper.UseTimeline(timeline);
        return new GlookoPumpEventMapper(ConnectorSource, timeMapper, NullLogger.Instance);
    }

    private static GlookoPumpEvent Evt(string? type, string? ts = "2026-03-18T00:03:12.000Z",
        string? guid = "g1", bool softDeleted = false) =>
        new() { Type = type, PumpTimestamp = ts, Guid = guid, SoftDeleted = softDeleted };

    [Fact]
    public void MapsKnownTypeToDeviceEventTypeAndKeysOnGuid()
    {
        var result = Mapper().TransformPumpEventsToDeviceEvents([Evt("reservoir_change", guid: "abc")]).Single();

        result.EventType.Should().Be(DeviceEventType.ReservoirChange);
        result.SyncIdentifier.Should().Be("glooko_event_abc");
        result.LegacyId.Should().Be("glooko_event_abc");
        result.DataSource.Should().Be(ConnectorSource);
        result.Notes.Should().Be("reservoir_change");
    }

    [Theory]
    [InlineData("set_site_change", DeviceEventType.SiteChange)]
    [InlineData("cannula_change", DeviceEventType.CannulaChange)]
    [InlineData("pod_change", DeviceEventType.PodChange)]
    [InlineData("rewind", DeviceEventType.Rewind)]
    [InlineData("pump_suspend", DeviceEventType.PumpSuspend)]
    [InlineData("time_changed", DeviceEventType.TimeChanged)]
    public void MapsVariousKnownTypes(string type, DeviceEventType expected)
    {
        Mapper().TransformPumpEventsToDeviceEvents([Evt(type)]).Single().EventType.Should().Be(expected);
    }

    [Fact]
    public void SkipsUnrecognisedAndSoftDeletedEvents()
    {
        var input = new[]
        {
            Evt("reservoir_change", guid: "keep"),
            Evt("some_future_event_kind", guid: "unknown"),
            Evt("reservoir_change", guid: "deleted", softDeleted: true),
            Evt(null, guid: "nulltype"),
        };

        var result = Mapper().TransformPumpEventsToDeviceEvents(input).ToList();

        result.Should().ContainSingle().Which.SyncIdentifier.Should().Be("glooko_event_keep");
    }

    [Fact]
    public void NullCollection_ReturnsEmpty()
    {
        Mapper().TransformPumpEventsToDeviceEvents(null).Should().BeEmpty();
    }

    [Fact]
    public void WithoutGuid_FallsBackToRawTypeAndTimestampKey()
    {
        var result = Mapper().TransformPumpEventsToDeviceEvents(
            [Evt("reservoir_change", ts: "2026-03-18T00:03:12.000Z", guid: null)]).Single();

        result.SyncIdentifier.Should().Be("glooko_event_raw_reservoir_change_2026-03-18T00:03:12.000Z");
    }

    [Fact]
    public void CorrectsFakeUtcUsingSydneyTimeline()
    {
        var timeline = new TimezoneTimeline(
        [
            new TimezoneTimelineEntry { Timezone = "Australia/Sydney", EffectiveFrom = DateTime.MinValue },
        ]);

        // Fake-UTC midnight 2026-01-10 = local midnight Sydney (AEDT +11) -> 2026-01-09 13:00Z.
        var result = Mapper(timeline).TransformPumpEventsToDeviceEvents(
            [Evt("reservoir_change", ts: "2026-01-10T00:00:00.000Z")]).Single();

        result.Timestamp.Should().Be(new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc));
    }
}
