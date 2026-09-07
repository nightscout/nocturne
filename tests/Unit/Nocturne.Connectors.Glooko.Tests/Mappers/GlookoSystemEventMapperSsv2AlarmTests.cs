using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoSystemEventMapper.TransformSsv2AlarmsToSystemEvents"/> — the SSV2
/// <c>pumps/alarms</c> feed (snake_case Mongo docs) → <see cref="SystemEvent"/>, the SSV2 counterpart
/// to the v3 <c>pumpAlarm</c> series.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoSystemEventMapperSsv2AlarmTests
{
    private readonly GlookoSystemEventMapper _mapper;

    public GlookoSystemEventMapperSsv2AlarmTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoSystemEventMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoSsv2Alarm Alarm(
        string? severity, string? value = "raw_occlusion", string? guid = "a-1",
        string? pumpTimestamp = "2026-06-20T08:00:00.000Z", bool softDeleted = false) => new()
    {
        PumpTimestamp = pumpTimestamp,
        Value = value,
        AlarmSeverity = severity,
        Guid = guid,
        SoftDeleted = softDeleted,
    };

    [Fact]
    public void Transform_MapsCodeCategoryAndGuidKeyedId()
    {
        var events = _mapper.TransformSsv2AlarmsToSystemEvents([Alarm("hazard", value: "raw_occlusion", guid: "abc")]);

        events.Should().ContainSingle();
        var e = events[0];
        e.Code.Should().Be("raw_occlusion");
        e.Category.Should().Be(SystemEventCategory.Pump);
        e.EventType.Should().Be(SystemEventType.Hazard);
        e.OriginalId.Should().Be("glooko_ssv2_alarm_abc");
        e.Source.Should().Be("glooko-connector");
        e.Mills.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("hazard", SystemEventType.Hazard)]
    [InlineData("warning", SystemEventType.Warning)]
    [InlineData("info", SystemEventType.Info)]
    [InlineData("critical", SystemEventType.Alarm)]   // unknown severity → most severe default
    [InlineData(null, SystemEventType.Alarm)]
    public void Transform_MapsSeverityToEventType(string? severity, SystemEventType expected)
    {
        var events = _mapper.TransformSsv2AlarmsToSystemEvents([Alarm(severity)]);
        events.Should().ContainSingle().Which.EventType.Should().Be(expected);
    }

    [Fact]
    public void Transform_SkipsSoftDeletedAndMissingTimestamp()
    {
        var events = _mapper.TransformSsv2AlarmsToSystemEvents([
            Alarm("hazard", softDeleted: true),
            Alarm("hazard", pumpTimestamp: null),
            Alarm("hazard", pumpTimestamp: "   "),
        ]);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Transform_NullInput_ReturnsEmpty()
    {
        _mapper.TransformSsv2AlarmsToSystemEvents(null).Should().BeEmpty();
    }
}
