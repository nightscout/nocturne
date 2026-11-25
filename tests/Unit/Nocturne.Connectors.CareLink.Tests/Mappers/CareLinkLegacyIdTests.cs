using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

/// <summary>
/// The stored legacy id of every CareLink record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class CareLinkLegacyIdTests
{
    [Fact]
    public void SensorGlucose()
    {
        var data = new CareLinkData
        {
            Sgs = [new CareLinkSensorGlucose { Sg = 120, Datetime = "2024-01-15T14:30:00", Kind = "SG" }],
            MedicalDeviceTime = "2024-01-15T14:30:00",
            CurrentServerTime = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            MedicalDeviceFamily = "BLE",
        };

        new CareLinkSensorGlucoseMapper(NullLogger.Instance).Map(data)
            .Single().LegacyId.Should().Be("carelink_1705325400000");
    }

    [Fact]
    public void Alarm()
    {
        var alarm = new CareLinkAlarm { Type = "PUMP_ALERT", Code = 102, Datetime = "2024-01-15T14:30:00" };

        CareLinkSystemEventMapper.Map(alarm, 0, 0)!.OriginalId.Should().Be("carelink_alarm_102_1705329000000");
    }

    [Fact]
    public void Notification()
    {
        var history = new CareLinkNotificationHistory
        {
            ActiveNotifications =
            [
                new CareLinkNotification { ReferenceGUID = "AAA-1", Type = "ALERT", TriggeredDateTime = "2026-06-10T11:00:00" },
            ],
        };

        CareLinkSystemEventMapper.MapNotifications(history, 0)
            .Single().OriginalId.Should().Be("carelink_notification_AAA-1");
    }

    [Fact]
    public void AutoBasal()
    {
        var data = new CareLinkData
        {
            Markers = [new CareLinkMarker { Type = "AUTO_BASAL_DELIVERY", DateTime = "2026-06-10T11:54:39", BolusAmount = 0.275 }],
        };

        CareLinkTreatmentMapper.MapTempBasals(data, 0)
            .Single().LegacyId.Should().Be("carelink_autobasal_1781092479000");
    }
}
