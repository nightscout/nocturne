using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps SSV2 <c>pumps/events</c> records to V4 <see cref="DeviceEvent"/>s. The Glooko <c>type</c> is a
///     snake_case kind that is mapped to a strongly-typed <see cref="DeviceEventType"/>; kinds we don't yet
///     recognise are skipped (and logged) rather than guessed, so no event is mis-categorised.
/// </summary>
public class GlookoPumpEventMapper
{
    private readonly string _connectorSource;
    private readonly GlookoTimeMapper _timeMapper;
    private readonly ILogger _logger;

    public GlookoPumpEventMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<DeviceEvent> TransformPumpEventsToDeviceEvents(IEnumerable<GlookoPumpEvent>? events)
    {
        var results = new List<DeviceEvent>();
        if (events == null) return results;

        var skipped = 0;
        foreach (var evt in events)
        {
            if (evt.SoftDeleted) continue;

            var eventType = MapEventType(evt.Type);
            if (eventType == null)
            {
                skipped++;
                continue;
            }

            var date = ParseTimestamp(evt.PumpTimestamp);
            if (date == null) continue;

            // Stable across timezone re-correction: prefer Glooko's guid, else the raw fake-UTC string.
            var key = !string.IsNullOrEmpty(evt.Guid)
                ? $"glooko_event_{evt.Guid}"
                : $"glooko_event_raw_{evt.Type}_{evt.PumpTimestamp}";

            var now = DateTime.UtcNow;
            results.Add(new DeviceEvent
            {
                Id = Guid.CreateVersion7(),
                Timestamp = date.Value,
                LegacyId = key,
                SyncIdentifier = key,
                Device = _connectorSource,
                DataSource = _connectorSource,
                EventType = eventType.Value,
                Notes = evt.Type,
                CreatedAt = now,
                ModifiedAt = now
            });
        }

        if (skipped > 0)
            _logger.LogInformation(
                "[{ConnectorSource}] Skipped {Count} pump events with unrecognised type", _connectorSource, skipped);

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} device events from SSV2 pumps/events", _connectorSource, results.Count);

        return results;
    }

    private DateTime? ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;

        // RoundtripKind keeps Glooko's fake-UTC wall-clock intact so the time mapper can correct it via
        // the timezone timeline (or static offset fallback), consistent with the other Glooko mappers.
        if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            _logger.LogWarning("[{ConnectorSource}] Failed to parse pump event timestamp '{Timestamp}'", _connectorSource, timestamp);
            return null;
        }

        return _timeMapper.GetCorrectedGlookoTime(parsed);
    }

    /// <summary>
    ///     Maps a Glooko event <c>type</c> to a <see cref="DeviceEventType"/>, or <c>null</c> when the kind
    ///     is not recognised (caller skips it). Extend this as new kinds are confirmed from live data.
    /// </summary>
    private static DeviceEventType? MapEventType(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "reservoir_change" => DeviceEventType.ReservoirChange,
            "set_site_change" or "site_change" or "infusion_set_change" => DeviceEventType.SiteChange,
            "cannula_change" or "cannula_fill" => DeviceEventType.CannulaChange,
            "pod_change" => DeviceEventType.PodChange,
            "pod_activated" => DeviceEventType.PodActivated,
            "pod_deactivated" => DeviceEventType.PodDeactivated,
            "insulin_change" => DeviceEventType.InsulinChange,
            "battery_change" or "pump_battery_change" => DeviceEventType.PumpBatteryChange,
            "rewind" => DeviceEventType.Rewind,
            "prime" or "tube_prime" or "tube_priming" => DeviceEventType.TubePriming,
            "needle_prime" or "needle_priming" => DeviceEventType.NeedlePriming,
            "suspend" or "pump_suspend" => DeviceEventType.PumpSuspend,
            "resume" or "pump_resume" => DeviceEventType.PumpResume,
            "date_changed" => DeviceEventType.DateChanged,
            "time_changed" => DeviceEventType.TimeChanged,
            "profile_switch" => DeviceEventType.ProfileSwitch,
            _ => null
        };
}
