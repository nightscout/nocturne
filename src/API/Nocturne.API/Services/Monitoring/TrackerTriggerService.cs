using System.Text.Json;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Advances tracker instances when a matching <see cref="DeviceEvent"/> is written: a site change
/// completes the running infusion-site instance and starts a new one, a sensor change does the same
/// for the sensor tracker, and so on for whatever a definition lists in its trigger event types.
/// </summary>
/// <remarks>
/// Registered as the <see cref="IDeviceEventReactor"/> adapter, so it runs from the device-event write
/// chokepoint and therefore fires for connector ingest and direct V4 writes alike.
/// </remarks>
/// <seealso cref="IDeviceEventReactor"/>
/// <seealso cref="TrackerDefinitionEntity.TriggerEventTypes"/>
public class TrackerTriggerService : IDeviceEventReactor
{
    /// <summary>
    /// The event types a definition may list in <see cref="TrackerDefinitionEntity.TriggerEventTypes"/>:
    /// the device events that mean something was changed. Pump suspend and resume are device events
    /// too, but pausing a pump ends nobody's sensor.
    /// </summary>
    public static readonly IReadOnlyList<string> TriggerableEventTypes =
    [
        TreatmentTypes.SensorStart,
        TreatmentTypes.SensorChange,
        TreatmentTypes.SensorStop,
        TreatmentTypes.TransmitterSensorInsert,
        TreatmentTypes.SiteChange,
        TreatmentTypes.CannulaChange,
        TreatmentTypes.PodChange,
        TreatmentTypes.InsulinChange,
        TreatmentTypes.ReservoirChangeEvent,
        TreatmentTypes.PumpBatteryChange,
    ];

    /// <summary>The <see cref="TriggerableEventTypes"/> spelling of <paramref name="name"/>, or null.</summary>
    public static string? CanonicalTrigger(string name) =>
        TriggerableEventTypes.FirstOrDefault(t => string.Equals(t, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Canonicalises a requested trigger list against <see cref="TriggerableEventTypes"/>. Blank entries
    /// are dropped, so a form can post one empty value to mean "none"; an unknown name is an error
    /// rather than a trigger that silently never fires, unless it is in <paramref name="alreadyStored"/>,
    /// where it predates the list and is dropped so the definition can still be saved. Null passes
    /// through as "not supplied".
    /// </summary>
    public static bool TryNormaliseTriggers(
        IEnumerable<string>? requested,
        IReadOnlyCollection<string> alreadyStored,
        out List<string>? normalised,
        out string? error)
    {
        normalised = null;
        error = null;
        if (requested is null)
            return true;

        normalised = [];
        foreach (var name in requested.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
        {
            var canonical = CanonicalTrigger(name);
            if (canonical is null)
            {
                if (alreadyStored.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                error = $"'{name}' is not an event a tracker can restart on";
                return false;
            }
            if (!normalised.Contains(canonical))
                normalised.Add(canonical);
        }
        return true;
    }

    private readonly ITrackerRepository _trackerRepository;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<TrackerTriggerService> _logger;

    public TrackerTriggerService(
        ITrackerRepository trackerRepository,
        ISignalRBroadcastService broadcast,
        ILogger<TrackerTriggerService> logger
    )
    {
        _trackerRepository = trackerRepository;
        _broadcast = broadcast;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default)
    {
        if (created.Count == 0)
            return;

        // Definitions are read tenant-wide rather than per-user: a device event is a fact about the
        // tenant's hardware, so every tracker configured to follow that event advances, and the new
        // instance is owned by whoever owns the definition. There is no user on a connector-ingested
        // event to scope this by, and the managed alert rules trackers sync are already tenant-visible.
        var definitions = await _trackerRepository.GetAllDefinitionsAsync(ct);

        var triggers = definitions
            .Select(definition => (Definition: definition, EventTypes: ParseTriggerEventTypes(definition.TriggerEventTypes)))
            // Event-mode trackers are scheduled against a calendar date and StartInstanceAsync needs a
            // ScheduledAt no device event can supply, so only Duration trackers are triggerable.
            .Where(t => t.EventTypes.Count > 0 && t.Definition.Mode == TrackerMode.Duration)
            .ToList();

        if (triggers.Count == 0)
            return;

        // Oldest first: one batch can carry several changes for the same tracker, and each is what
        // completes the instance the previous one started.
        foreach (var deviceEvent in created.OrderBy(e => e.Timestamp))
        {
            foreach (var (definition, eventTypes) in triggers)
            {
                if (!eventTypes.Contains(deviceEvent.EventType))
                    continue;

                if (!MatchesNotesFilter(definition, deviceEvent))
                    continue;

                await AdvanceAsync(definition, deviceEvent, ct);
            }
        }
    }

    /// <summary>
    /// Completes whatever is running for the definition and starts a fresh instance at the event's
    /// timestamp, unless a guard says this event does not represent a new change.
    /// </summary>
    private async Task AdvanceAsync(
        TrackerDefinitionEntity definition,
        DeviceEvent deviceEvent,
        CancellationToken ct
    )
    {
        var startTreatmentId = deviceEvent.Id.ToString();

        // Connectors re-publish a moving window, so the same change can reach the chokepoint more than
        // once. An instance already started from this event means it has been handled. Connectors also
        // backfill older events into that window, and one dated at or before the running instance is
        // history rather than a new change.
        var succession = await TrackerSuccession.StartAsync(
            _trackerRepository,
            _broadcast,
            _logger,
            definition,
            deviceEvent.Timestamp,
            completionNotes: $"Auto-completed by {deviceEvent.EventType}",
            completeTreatmentId: startTreatmentId,
            token => _trackerRepository.StartInstanceAsync(
                definition.Id,
                definition.UserId,
                startNotes: null,
                startTreatmentId: startTreatmentId,
                startedAt: deviceEvent.Timestamp,
                cancellationToken: token
            ),
            ct,
            alreadyStarted: running => running.Any(i => i.StartTreatmentId == startTreatmentId)
        );
        if (succession.Started is not { } newInstance)
        {
            _logger.LogDebug(
                "Skipping tracker {DefinitionName} for device event at {EventTime}: {Outcome}",
                definition.Name,
                deviceEvent.Timestamp,
                succession.Outcome
            );
            return;
        }

        _logger.LogInformation(
            "Auto-started tracker instance {InstanceId} for {DefinitionName} on {EventType}",
            newInstance.Id,
            definition.Name,
            deviceEvent.EventType
        );

        await _broadcast.BroadcastTrackerUpdateAsync(
            "create",
            TrackerInstanceDto.FromEntity(newInstance),
            definition.UserId,
            definition.Visibility
        );
    }

    /// <summary>
    /// Applies the definition's optional notes substring filter, which distinguishes trackers that share
    /// an event type (two pump sites logged as "Site Change" with different notes, say).
    /// </summary>
    private static bool MatchesNotesFilter(TrackerDefinitionEntity definition, DeviceEvent deviceEvent)
    {
        if (string.IsNullOrWhiteSpace(definition.TriggerNotesContains))
            return true;

        return deviceEvent.Notes is not null
            && deviceEvent.Notes.Contains(definition.TriggerNotesContains, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a definition's trigger list, which stores legacy Nightscout event-type strings, and resolves
    /// them to the typed device event types they correspond to. Entries naming something that is not a
    /// device event (a bolus type, say) map to nothing and are dropped.
    /// </summary>
    private static HashSet<DeviceEventType> ParseTriggerEventTypes(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        List<string>? eventTypes;
        try
        {
            eventTypes = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (eventTypes is null)
            return [];

        return [.. eventTypes
            .Select(name => TreatmentTypes.DeviceEventTypeMap.TryGetValue(name, out var parsed)
                ? parsed
                : (DeviceEventType?)null)
            .OfType<DeviceEventType>()];
    }
}
