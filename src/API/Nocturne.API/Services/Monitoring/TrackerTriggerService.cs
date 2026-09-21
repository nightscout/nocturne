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
        var activeInstances = await _trackerRepository.GetActiveInstancesForDefinitionAsync(definition.Id, ct);

        // Connectors re-publish a moving window, so the same change can reach the chokepoint more than
        // once. An instance already started from this event means it has been handled.
        if (activeInstances.Any(i => i.StartTreatmentId == startTreatmentId))
            return;

        // Connectors also backfill older events into that window. One dated at or before the running
        // instance is history rather than a new change, and acting on it would rewind the tracker to
        // an earlier consumable.
        if (activeInstances.Any(i => i.StartedAt >= deviceEvent.Timestamp))
        {
            _logger.LogDebug(
                "Skipping tracker {DefinitionName}: device event at {EventTime} is not newer than the active instance",
                definition.Name,
                deviceEvent.Timestamp
            );
            return;
        }

        foreach (var activeInstance in activeInstances)
        {
            var completed = await _trackerRepository.CompleteInstanceAsync(
                activeInstance.Id,
                ReasonFor(definition, activeInstance.StartedAt, deviceEvent.Timestamp),
                completionNotes: $"Auto-completed by {deviceEvent.EventType}",
                completeTreatmentId: startTreatmentId,
                completedAt: deviceEvent.Timestamp,
                cancellationToken: ct
            );

            if (completed is null)
                continue;

            _logger.LogInformation(
                "Auto-completed tracker instance {InstanceId} for {DefinitionName} on {EventType}",
                completed.Id,
                definition.Name,
                deviceEvent.EventType
            );

            await _broadcast.BroadcastTrackerUpdateAsync("complete", TrackerInstanceDto.FromEntity(completed));
        }

        var newInstance = await _trackerRepository.StartInstanceAsync(
            definition.Id,
            definition.UserId,
            startNotes: null,
            startTreatmentId: startTreatmentId,
            startedAt: deviceEvent.Timestamp,
            cancellationToken: ct
        );

        _logger.LogInformation(
            "Auto-started tracker instance {InstanceId} for {DefinitionName} on {EventType}",
            newInstance.Id,
            definition.Name,
            deviceEvent.EventType
        );

        await _broadcast.BroadcastTrackerUpdateAsync("create", TrackerInstanceDto.FromEntity(newInstance));
    }

    /// <summary>
    /// Classifies an auto-completion the way the history surface reads it: a consumable that reached its
    /// configured lifespan expired, one swapped before that was replaced early. A definition with no
    /// lifespan has no term to have reached, so it is a plain completion.
    /// </summary>
    private static CompletionReason ReasonFor(TrackerDefinitionEntity definition, DateTime startedAt, DateTime completedAt)
    {
        if (definition.LifespanHours is not { } lifespanHours || lifespanHours <= 0)
            return CompletionReason.Completed;

        return completedAt - startedAt >= TimeSpan.FromHours(lifespanHours)
            ? CompletionReason.Expired
            : CompletionReason.ReplacedEarly;
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
