using FluentAssertions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

public class CareLinkSystemEventMapperTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Map_ReturnsSystemEvent_ForValidAlarm()
    {
        var alarm = new CareLinkAlarm
        {
            Type = "PUMP_ALERT", Code = 102, Datetime = "2024-01-15T14:30:00", Flash = true,
        };
        var serverTimeMs = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var pumpOffsetMs = TimeSpan.FromHours(1).TotalMilliseconds;

        var result = CareLinkSystemEventMapper.Map(alarm, pumpOffsetMs, serverTimeMs, Now);

        result.Should().NotBeNull();
        result!.EventType.Should().Be(SystemEventType.Alarm);
        result.Category.Should().Be(SystemEventCategory.Pump);
        result.Code.Should().Be("102");
        result.Description.Should().Be("PUMP_ALERT");
        result.Source.Should().Be(DataSources.CareLinkConnector);
    }

    [Fact]
    public void Map_StampsCreatedAtFromTheGivenInstant()
    {
        var alarm = new CareLinkAlarm { Type = "PUMP_ALERT", Code = 102, Datetime = "2024-01-15T14:30:00" };

        var result = CareLinkSystemEventMapper.Map(alarm, 0, 0, Now);

        result!.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void MapNotifications_StampsEveryEventWithTheBatchInstant()
    {
        var history = new CareLinkNotificationHistory
        {
            ActiveNotifications =
            [
                new CareLinkNotification { ReferenceGUID = "AAA", Type = "ALERT", TriggeredDateTime = "2026-06-10T11:00:00" },
                new CareLinkNotification { ReferenceGUID = "BBB", Type = "ALERT", TriggeredDateTime = "2026-06-10T11:05:00" },
            ],
        };

        var events = CareLinkSystemEventMapper.MapNotifications(history, 0, Now);

        events.Should().HaveCount(2).And.OnlyContain(e => e.CreatedAt == Now);
    }

    [Fact]
    public void Map_ReturnsNull_WhenAlarmIsNull()
    {
        CareLinkSystemEventMapper.Map(null, 0, 0, Now).Should().BeNull();
    }

    [Fact]
    public void Map_ReturnsNull_WhenDatetimeIsMissing()
    {
        var alarm = new CareLinkAlarm { Type = "ALERT", Code = 1 };
        CareLinkSystemEventMapper.Map(alarm, 0, 0, Now).Should().BeNull();
    }

    [Fact]
    public void MapNotifications_MapsActiveAndCleared_WithReferenceGuidDedupId()
    {
        var history = new CareLinkNotificationHistory
        {
            ActiveNotifications =
            [
                new CareLinkNotification
                {
                    ReferenceGUID = "AAA", Type = "ALERT", FaultId = 105,
                    MessageId = "BC_MESSAGE_TIME_REMAINING_CHANGE_RESERVOIR",
                    TriggeredDateTime = "2026-06-10T11:00:00", UnitsRemaining = 19.8,
                },
            ],
            ClearedNotifications =
            [
                new CareLinkNotification
                {
                    ReferenceGUID = "BBB", Type = "INFO", FaultId = 7,
                    MessageId = "SOMETHING", DateTime = "2026-06-10T10:00:00",
                },
            ],
        };

        var events = CareLinkSystemEventMapper.MapNotifications(history, 0, Now);

        events.Should().HaveCount(2);

        var alert = events.Single(e => e.OriginalId == "carelink_notification_AAA");
        alert.EventType.Should().Be(SystemEventType.Alarm);
        alert.Category.Should().Be(SystemEventCategory.Pump);
        alert.Code.Should().Be("105");
        alert.Description.Should().Be("BC_MESSAGE_TIME_REMAINING_CHANGE_RESERVOIR");
        alert.Source.Should().Be(DataSources.CareLinkConnector);
        alert.Mills.Should().Be(new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        alert.Metadata.Should().ContainKey("unitsRemaining");

        var info = events.Single(e => e.OriginalId == "carelink_notification_BBB");
        info.EventType.Should().Be(SystemEventType.Info);
    }

    [Fact]
    public void MapNotifications_DedupsByReferenceGuid_AcrossActiveAndCleared()
    {
        var history = new CareLinkNotificationHistory
        {
            ActiveNotifications =
            [
                new CareLinkNotification { ReferenceGUID = "DUP", Type = "ALERT", TriggeredDateTime = "2026-06-10T11:00:00" },
            ],
            ClearedNotifications =
            [
                new CareLinkNotification { ReferenceGUID = "DUP", Type = "ALERT", TriggeredDateTime = "2026-06-10T11:00:00" },
            ],
        };

        CareLinkSystemEventMapper.MapNotifications(history, 0, Now).Should().HaveCount(1);
    }

    [Fact]
    public void MapNotifications_SkipsNotificationsWithoutReferenceGuid()
    {
        var history = new CareLinkNotificationHistory
        {
            ActiveNotifications =
            [
                new CareLinkNotification { Type = "ALERT", TriggeredDateTime = "2026-06-10T11:00:00" },
            ],
        };

        CareLinkSystemEventMapper.MapNotifications(history, 0, Now).Should().BeEmpty();
    }

    [Fact]
    public void MapNotifications_ReturnsEmpty_WhenHistoryIsNull()
    {
        CareLinkSystemEventMapper.MapNotifications(null, 0, Now).Should().BeEmpty();
    }
}
