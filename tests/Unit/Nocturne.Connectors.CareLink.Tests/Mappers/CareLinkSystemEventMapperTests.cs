using FluentAssertions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

public class CareLinkSystemEventMapperTests
{
    [Fact]
    public void Map_ReturnsSystemEvent_ForValidAlarm()
    {
        var alarm = new CareLinkAlarm
        {
            Type = "PUMP_ALERT", Code = 102, Datetime = "2024-01-15T14:30:00", Flash = true,
        };
        var serverTimeMs = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var pumpOffsetMs = TimeSpan.FromHours(1).TotalMilliseconds;

        var result = CareLinkSystemEventMapper.Map(alarm, pumpOffsetMs, serverTimeMs);

        result.Should().NotBeNull();
        result!.EventType.Should().Be(SystemEventType.Alarm);
        result.Category.Should().Be(SystemEventCategory.Pump);
        result.Code.Should().Be("102");
        result.Description.Should().Be("PUMP_ALERT");
        result.Source.Should().Be(DataSources.CareLinkConnector);
    }

    [Fact]
    public void Map_ReturnsNull_WhenAlarmIsNull()
    {
        CareLinkSystemEventMapper.Map(null, 0, 0).Should().BeNull();
    }

    [Fact]
    public void Map_ReturnsNull_WhenDatetimeIsMissing()
    {
        var alarm = new CareLinkAlarm { Type = "ALERT", Code = 1 };
        CareLinkSystemEventMapper.Map(alarm, 0, 0).Should().BeNull();
    }
}
