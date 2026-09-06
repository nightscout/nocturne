using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps Tandem alarm, malfunction and CGM-alert events to <see cref="SystemEvent"/> records.
/// Mirrors <c>tconnectsync</c>'s <c>process_alarm.py</c> and <c>process_cgm_alert.py</c>, including
/// the skipped resume-pump alarms and Dexcom out-of-range alerts.
/// </summary>
public sealed class TandemSystemEventMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    private static readonly HashSet<string> SkippedAlarms =
        ["RESUME_PUMP_ALARM", "RESUME_PUMP_ALARM2"];

    public List<SystemEvent> Map(IEnumerable<TandemPumpEvent> events)
    {
        var records = events
            .Select(MapEvent)
            .Where(record => record != null)
            .Select(record => record!)
            .ToList();

        _logger.LogDebug("Mapped {Count} Tandem system events", records.Count);
        return records;
    }

    private SystemEvent? MapEvent(TandemPumpEvent ev) => ev.Name switch
    {
        "LID_ALARM_ACTIVATED" => MapAlarm(ev),
        "LID_MALFUNCTION_ACTIVATED" => Build(ev, SystemEventType.Hazard, SystemEventCategory.Pump,
            ev.Raw("MalfID"), "Malfunction"),
        "LID_CGM_ALERT_ACTIVATED" => MapCgmAlert(ev, "CGM Alert"),
        "LID_CGM_ALERT_ACTIVATED_DEX" => MapDexCgmAlert(ev),
        "LID_CGM_ALERT_ACTIVATED_FSL2" => MapCgmAlert(ev, "Libre CGM Alert"),
        _ => null,
    };

    private SystemEvent? MapAlarm(TandemPumpEvent ev)
    {
        var name = ev.EnumName("AlarmID");
        if (name != null && SkippedAlarms.Contains(name))
            return null;

        return Build(ev, SystemEventType.Alarm, SystemEventCategory.Pump, ev.Raw("AlarmID"),
            name ?? "Pump Alarm");
    }

    private SystemEvent? MapCgmAlert(TandemPumpEvent ev, string prefix)
    {
        var name = ev.EnumName("DalertID");
        if (name == null)
            return null; // Unknown alert code: skip, matching tconnectsync.

        return Build(ev, SystemEventType.Warning, SystemEventCategory.Cgm, ev.Raw("DalertID"),
            $"{prefix} ({name})");
    }

    private SystemEvent? MapDexCgmAlert(TandemPumpEvent ev)
    {
        var name = ev.EnumName("DalertID");
        if (name == null || name == "CGM Out Of Range")
            return null;

        return Build(ev, SystemEventType.Warning, SystemEventCategory.Cgm, ev.Raw("DalertID"),
            $"Dexcom CGM Alert ({name})");
    }

    private SystemEvent Build(
        TandemPumpEvent ev, SystemEventType type, SystemEventCategory category, long? code, string description) =>
        new()
        {
            EventType = type,
            Category = category,
            Code = code?.ToString(),
            Description = description,
            Mills = TandemMapHelpers.ToMills(_time.ToUtc(ev.RawTimestampSeconds)),
            Source = TandemMapHelpers.Source,
            OriginalId = $"tandem_sysevent_{ev.SeqNum}",
        };
}
