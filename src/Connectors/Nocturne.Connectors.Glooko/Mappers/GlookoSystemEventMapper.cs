using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoSystemEventMapper
{
    private readonly string _connectorSource;
    private readonly ILogger _logger;
    private readonly GlookoTimeMapper _timeMapper;

    public GlookoSystemEventMapper(
        string connectorSource,
        GlookoTimeMapper timeMapper,
        ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<SystemEvent> TransformV3ToSystemEvents(GlookoV3GraphResponse graphData)
    {
        var events = new List<SystemEvent>();

        if (graphData?.Series == null)
            return events;

        var series = graphData.Series;

        if (series.PumpAlarm != null)
            foreach (var alarm in series.PumpAlarm)
            {
                var timestamp = _timeMapper.GetCorrectedGlookoTime(alarm.X);
                var eventType = DetermineAlarmEventType(alarm.AlarmType, alarm.Data?.AlarmCode);

                events.Add(
                    new SystemEvent
                    {
                        OriginalId = $"glooko_alarm_{alarm.X}",
                        EventType = eventType,
                        Category = SystemEventCategory.Pump,
                        Code = alarm.Data?.AlarmCode ?? alarm.AlarmType,
                        Description =
                            alarm.Data?.AlarmDescription
                            ?? alarm.Label
                            ?? alarm.AlarmType
                            ?? "Unknown alarm",
                        Mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(),
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "alarmType", alarm.AlarmType ?? "unknown" },
                            { "label", alarm.Label ?? "" }
                        }
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} system events from v3 data",
            _connectorSource,
            events.Count
        );

        return events;
    }

    private static SystemEventType DetermineAlarmEventType(string? alarmType, string? alarmCode)
    {
        var type = (alarmType ?? "").ToUpperInvariant();
        var code = (alarmCode ?? "").ToUpperInvariant();

        if (
            type.Contains("OCCLUSION")
            || type.Contains("EMPTY")
            || code.Contains("OCCLUSION")
            || code.Contains("EMPTY")
        )
            return SystemEventType.Alarm;

        if (
            type.Contains("LOW")
            || type.Contains("SENSOR")
            || type.Contains("BATTERY")
            || code.Contains("LOW")
            || code.Contains("SENSOR")
        )
            return SystemEventType.Warning;

        return SystemEventType.Info;
    }
}