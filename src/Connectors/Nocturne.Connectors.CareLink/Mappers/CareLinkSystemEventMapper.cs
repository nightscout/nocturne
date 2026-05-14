using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.CareLink.Mappers;

public static class CareLinkSystemEventMapper
{
    public static SystemEvent? Map(CareLinkAlarm? alarm, double pumpOffsetMs, long serverTimeMs)
    {
        if (alarm == null || string.IsNullOrEmpty(alarm.Datetime))
            return null;

        var timestamp = CareLinkTimestampParser.ParseSgTimestamp(alarm.Datetime, pumpOffsetMs);
        if (timestamp == null)
            return null;

        var mills = new DateTimeOffset(timestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return new SystemEvent
        {
            Id = Guid.CreateVersion7().ToString(),
            EventType = SystemEventType.Alarm,
            Category = SystemEventCategory.Pump,
            Code = alarm.Code.ToString(),
            Description = alarm.Type,
            Mills = mills,
            Source = DataSources.CareLinkConnector,
            OriginalId = $"carelink_alarm_{alarm.Code}_{mills}",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["flash"] = alarm.Flash,
            },
        };
    }
}
