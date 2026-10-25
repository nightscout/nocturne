using System.Text.Json;
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
/// Deleting the event undoes the advance (<see cref="OnDeletedAsync"/>).
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
    /// rather than a trigger that silently never fires. Null passes through as "not supplied".
    /// </summary>
    public static bool TryNormaliseTriggers(
        IEnumerable<string>? requested,
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
    public Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default)
        => AdvanceAllAsync(created, replay: false, ct);

    private async Task AdvanceAllAsync(IReadOnlyList<DeviceEvent> created, bool replay, CancellationToken ct)
    {
        if (created.Count == 0)
            return;

        // Every tracker configured to follow the event advances: a device event is a fact about the
        // tenant's hardware, and trackers are the tenant's. The new instance records the definition's
        // creator, since a connector-ingested event carries no user.
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

                await AdvanceAsync(definition, deviceEvent, replay, ct);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A deleted change never happened, so the run it started is undone and the run it replaced
    /// resumes (<see cref="ITrackerRepository.RevertStartAsync"/>). Only a run still going is undone:
    /// once a later change has replaced it, it is the tracker's history, unless that change is
    /// deleted too. The run is found by the event's id alone, so it is undone even when its
    /// definition no longer follows that kind of event.
    /// </remarks>
    public async Task OnDeletedAsync(IReadOnlyList<DeviceEvent> deleted, CancellationToken ct = default)
    {
        if (deleted.Count == 0)
            return;

        var deletedIds = deleted.Select(e => e.Id.ToString()).ToHashSet();
        // Newest first, so of a chain of deleted changes each undo exposes the run the next one started.
        var pending = new Stack<TrackerInstanceEntity>(
            (await _trackerRepository.GetRunningStartedByAsync(deletedIds, ct)).OrderBy(i => i.StartedAt));

        while (pending.TryPop(out var run))
        {
            var revert = await _trackerRepository.ExecuteUnderDefinitionLockAsync(
                run.DefinitionId,
                (tx, token) => tx.RevertStartAsync(run.DefinitionId, run.StartTreatmentId!, token),
                async (tx, attempt, token) =>
                    attempt is not null && await tx.GetInstanceByIdAsync(attempt.Removed.Id, token) is null,
                ct);
            if (revert is null)
                continue;

            _logger.LogInformation(
                "Undid tracker instance {InstanceId} for {DefinitionName}: the change that started it was deleted",
                revert.Removed.Id,
                run.Definition.Name);

            await _broadcast.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Delete, TrackerInstanceDto.FromEntity(revert.Removed));
            foreach (var reopened in revert.Reopened)
            {
                await _broadcast.BroadcastTrackerUpdateAsync(
                    TrackerRealtimeAction.Reopen, TrackerInstanceDto.FromEntity(reopened));
                if (reopened.StartTreatmentId is { } startedBy && deletedIds.Contains(startedBy))
                    pending.Push(reopened);
            }
        }
    }

    /// <inheritdoc />
    public Task OnReplayedAsync(IReadOnlyList<DeviceEvent> events, CancellationToken ct = default)
        => AdvanceAllAsync(events, replay: true, ct);

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetFollowedAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default)
    {
        if (eventIds.Count == 0)
            return new HashSet<Guid>();

        var running = await _trackerRepository.GetRunningStartedByAsync(eventIds.Select(id => id.ToString()).ToList(), ct);
        return running
            .Select(i => Guid.TryParse(i.StartTreatmentId, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
    }

    /// <summary>
    /// Completes whatever is running for the definition and starts a fresh instance at the event's
    /// timestamp, unless a guard says this event does not represent a new change.
    /// </summary>
    private async Task AdvanceAsync(
        TrackerDefinitionEntity definition,
        DeviceEvent deviceEvent,
        bool replay,
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
            (tx, token) => tx.StartInstanceAsync(
                definition.Id,
                definition.CreatedBy,
                startNotes: null,
                startTreatmentId: startTreatmentId,
                startedAt: deviceEvent.Timestamp,
                cancellationToken: token
            ),
            ct,
            alreadyStarted: running => running.Any(i => i.StartTreatmentId == startTreatmentId),
            replay
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
    /// Reads a definition's trigger list, which stores Nightscout event-type names, and resolves the
    /// ones in <see cref="TriggerableEventTypes"/> (through <see cref="CanonicalTrigger"/>, as the
    /// definition DTO does) to their device event types. Any other stored name is ignored.
    /// </summary>
    internal static HashSet<DeviceEventType> ParseTriggerEventTypes(string? json)
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

        return eventTypes is null ? [] : ParseTriggerEventTypes(eventTypes);
    }

    /// <inheritdoc cref="ParseTriggerEventTypes(string?)"/>
    internal static HashSet<DeviceEventType> ParseTriggerEventTypes(IEnumerable<string> eventTypes) =>
        [.. eventTypes
            .Select(CanonicalTrigger)
            .OfType<string>()
            .Select(name => TreatmentTypes.DeviceEventTypeMap.TryGetValue(name, out var parsed)
                ? parsed
                : (DeviceEventType?)null)
            .OfType<DeviceEventType>()];
}
