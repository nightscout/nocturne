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
/// Covers <see cref="GlookoSystemEventMapper.TransformV3ToSystemEvents"/> — the v3 graph
/// <c>pumpAlarm</c> series → <see cref="SystemEvent"/>. Glooko populates the point's <c>name</c>
/// and <c>alarmSeverity</c> (not the older <c>alarmType</c>/<c>data.alarmCode</c>/<c>label</c>
/// fields), so an alarm must carry its real name and severity through rather than collapsing to an
/// Info "Unknown alarm".
/// </summary>
[Trait("Category", "Unit")]
public class GlookoSystemEventMapperV3AlarmTests
{
    private readonly GlookoSystemEventMapper _mapper;

    public GlookoSystemEventMapperV3AlarmTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoSystemEventMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoV3GraphResponse Response(params GlookoV3AlarmDataPoint[] alarms) => new()
    {
        Series = new GlookoV3Series { PumpAlarm = alarms },
    };

    private static GlookoV3AlarmDataPoint Alarm(string? name, string? severity, long x = 1_783_982_636) => new()
    {
        X = x,
        Name = name,
        AlarmSeverity = severity,
    };

    [Fact]
    public void Transform_CarriesNameAndSeverity_NotUnknownAlarm()
    {
        var events = _mapper.TransformV3ToSystemEvents(Response(Alarm("Occlusion", "hazard")));

        events.Should().ContainSingle();
        var e = events[0];
        e.Code.Should().Be("Occlusion");
        e.Description.Should().Be("Occlusion");
        e.Category.Should().Be(SystemEventCategory.Pump);
        e.EventType.Should().Be(SystemEventType.Hazard);
        e.Source.Should().Be("glooko-connector");
        e.Mills.Should().BeGreaterThan(0);
        e.Metadata!["severity"].Should().Be("hazard");
    }

    [Theory]
    [InlineData("hazard", SystemEventType.Hazard)]
    [InlineData("warning", SystemEventType.Warning)]
    [InlineData("info", SystemEventType.Info)]
    [InlineData("critical", SystemEventType.Alarm)] // unknown severity string → most severe default
    public void Transform_MapsSeverityToEventType(string severity, SystemEventType expected)
    {
        var events = _mapper.TransformV3ToSystemEvents(Response(Alarm("Low Battery", severity)));

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(expected);
    }

    [Fact]
    public void Transform_WithoutSeverity_FallsBackToNameKeywordHeuristic()
    {
        // A payload missing alarmSeverity still classifies by the alarm name: an occlusion is an
        // Alarm, a low battery a Warning.
        var events = _mapper.TransformV3ToSystemEvents(Response(
            Alarm("Occlusion", severity: null, x: 100),
            Alarm("Low Battery", severity: null, x: 200)));

        events.Should().HaveCount(2);
        events.Single(e => e.Code == "Occlusion").EventType.Should().Be(SystemEventType.Alarm);
        events.Single(e => e.Code == "Low Battery").EventType.Should().Be(SystemEventType.Warning);
    }

    [Fact]
    public void Transform_FallsBackToLegacyFields_WhenNameAbsent()
    {
        // Older/other payload shapes that only carry data.alarmCode + alarmDescription still map.
        var events = _mapper.TransformV3ToSystemEvents(Response(new GlookoV3AlarmDataPoint
        {
            X = 1_783_982_636,
            Data = new GlookoV3AlarmData { AlarmCode = "occlusion", AlarmDescription = "Pump occluded" },
        }));

        events.Should().ContainSingle();
        var e = events[0];
        e.Code.Should().Be("occlusion");
        e.Description.Should().Be("Pump occluded");
        e.EventType.Should().Be(SystemEventType.Alarm);
    }
}
