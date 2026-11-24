using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.CareLink.Mappers;

public static class CareLinkSystemEventMapper
{
    private static readonly ConnectorLegacyIds LegacyIds = ConnectorLegacyIds.For<CareLinkConnectorConfiguration>();

    public static SystemEvent? Map(CareLinkAlarm? alarm, double pumpOffsetMs, long serverTimeMs)
    {
        if (alarm == null || string.IsNullOrEmpty(alarm.Datetime))
            return null;

        var timestamp = CareLinkTimestampParser.ParseSgTimestamp(alarm.Datetime, pumpOffsetMs);
        if (timestamp == null)
            return null;

        var mills = timestamp.Value.ToMills();

        return new SystemEvent
        {
            EventType = SystemEventType.Alarm,
            Category = SystemEventCategory.Pump,
            Code = alarm.Code.ToString(),
            Description = alarm.Type,
            Mills = mills,
            OriginalId = LegacyIds.Of("alarm", alarm.Code, mills),
            Metadata = new Dictionary<string, object>
            {
                ["flash"] = alarm.Flash,
            },
        };
    }

    /// <summary>
    /// Maps the periodic payload's notification history (active + cleared) to <see cref="SystemEvent"/>
    /// records. Deduped by <c>referenceGUID</c> so the every-5-min refetch upserts rather than
    /// duplicates. Notifications without a stable <c>referenceGUID</c> are skipped (no reliable key).
    /// </summary>
    public static List<SystemEvent> MapNotifications(
        CareLinkNotificationHistory? history, double pumpOffsetMs)
    {
        if (history == null)
            return [];

        var results = new List<SystemEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var notification in (history.ActiveNotifications ?? []).Concat(history.ClearedNotifications ?? []))
        {
            var systemEvent = MapNotification(notification, pumpOffsetMs);
            if (systemEvent?.OriginalId != null && seen.Add(systemEvent.OriginalId))
                results.Add(systemEvent);
        }

        return results;
    }

    private static SystemEvent? MapNotification(CareLinkNotification notification, double pumpOffsetMs)
    {
        if (string.IsNullOrEmpty(notification.ReferenceGUID))
            return null;

        var timestamp = CareLinkTimestampParser.ParseSgTimestamp(
            notification.TriggeredDateTime ?? notification.DateTime, pumpOffsetMs);
        if (timestamp == null)
            return null;

        var mills = timestamp.Value.ToMills();

        var metadata = new Dictionary<string, object>();
        if (notification.UnitsRemaining.HasValue)
            metadata["unitsRemaining"] = notification.UnitsRemaining.Value;

        return new SystemEvent
        {
            EventType = string.Equals(notification.Type, "ALERT", StringComparison.OrdinalIgnoreCase)
                ? SystemEventType.Alarm
                : SystemEventType.Info,
            Category = SystemEventCategory.Pump,
            Code = notification.FaultId?.ToString(),
            Description = notification.MessageId,
            Mills = mills,
            OriginalId = LegacyIds.Of("notification", notification.ReferenceGUID),
            Metadata = metadata.Count > 0 ? metadata : null,
        };
    }
}
