using Nocturne.API.Services.Realtime;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Advances tracker instances when a matching <see cref="DeviceEvent"/> is written: a site change
/// completes the running infusion-site instance and starts a new one, a sensor change does the same
/// for the sensor tracker, and so on for whatever a definition lists in its trigger event types. An
/// event in its end event types (a pod deactivated, say) completes the running instance and starts
/// nothing. Deleting the event undoes either (<see cref="OnDeletedAsync"/>).
/// </summary>
/// <remarks>
/// Registered as the <see cref="IDeviceEventReactor"/> adapter, so it runs from the device-event write
/// chokepoint and therefore fires for connector ingest and direct V4 writes alike. It passes over the
/// events <see cref="TrackerDeviceEventWriter"/> records.
/// </remarks>
/// <seealso cref="IDeviceEventReactor"/>
/// <seealso cref="TrackerDefinition.TriggerEventTypes"/>
public class TrackerTriggerService : IDeviceEventReactor
{
    private readonly ITrackerRepository _trackerRepository;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly TimeProvider _time;
    private readonly ILogger<TrackerTriggerService> _logger;

    public TrackerTriggerService(
        ITrackerRepository trackerRepository,
        ISignalRBroadcastService broadcast,
        TimeProvider timeProvider,
        ILogger<TrackerTriggerService> logger
    )
    {
        _trackerRepository = trackerRepository;
        _broadcast = broadcast;
        _time = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

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
            .Select(definition => (
                Definition: definition,
                Starts: ParseTriggerEventTypes(definition.TriggerEventTypes),
                Ends: ParseEndEventTypes(definition.EndEventTypes)))
            // Event-mode trackers are scheduled against a calendar date and StartInstanceAsync needs a
            // ScheduledAt no device event can supply, so only Duration trackers are triggerable.
            .Where(t => (t.Starts.Count > 0 || t.Ends.Count > 0) && t.Definition.Mode == TrackerMode.Duration)
            .ToList();

        if (triggers.Count == 0)
            return;

        // Oldest first: one batch can carry several changes for the same tracker, and each is what
        // completes the instance the previous one started.
        foreach (var deviceEvent in created
            .Where(e => e.DataSource != DataSources.Tracker)
            .OrderBy(e => e.Timestamp))
        {
            foreach (var (definition, starts, ends) in triggers)
            {
                var starting = starts.Contains(deviceEvent.EventType);
                if (!starting && !ends.Contains(deviceEvent.EventType))
                    continue;

                if (!MatchesNotesFilter(definition, deviceEvent))
                    continue;

                if (starting)
                    await AdvanceAsync(definition, deviceEvent, replay, ct);
                else
                    await EndAsync(definition, deviceEvent, ct);
            }
        }
    }

    /// <summary>
    /// Completes whatever is running for the definition at the event's timestamp and starts nothing.
    /// A run started at or after that moment is newer than the end, so it is left running; that also
    /// makes a re-published or backfilled end a no-op once the next run has started.
    /// </summary>
    private async Task EndAsync(TrackerDefinition definition, DeviceEvent deviceEvent, CancellationToken ct)
    {
        var endedBy = deviceEvent.Id.ToString();
        var ended = await _trackerRepository.ExecuteUnderDefinitionLockAsync(
            definition.Id,
            async (tx, token) =>
            {
                List<TrackerInstance> completed = [];
                foreach (var run in await tx.GetActiveInstancesForDefinitionAsync(definition.Id, token))
                {
                    if (run.StartedAt >= deviceEvent.Timestamp)
                        continue;

                    var won = await tx.CompleteInstanceAsync(
                        run.Id,
                        TrackerSchedule.ReasonForReplacement(definition.LifespanHours, run.StartedAt, deviceEvent.Timestamp),
                        $"Auto-completed by {deviceEvent.EventType}",
                        endedBy,
                        deviceEvent.Timestamp,
                        token);
                    if (won is not null)
                        completed.Add(won);
                }
                return completed;
            },
            async (tx, attempt, token) =>
            {
                foreach (var instance in attempt)
                    if (await tx.GetInstanceByIdAsync(instance.Id, token) is not { CompletedAt: not null })
                        return false;
                return true;
            },
            ct);

        foreach (var instance in ended)
        {
            _logger.LogInformation(
                "Ended tracker instance {InstanceId} for {DefinitionName} on {EventType}",
                instance.Id,
                definition.Name,
                deviceEvent.EventType);
            await _broadcast.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Complete, TrackerInstanceDto.From(instance, UtcNow));
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
        var pending = new Stack<TrackerInstance>(
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
                TrackerRealtimeAction.Delete, TrackerInstanceDto.From(revert.Removed, UtcNow));
            foreach (var reopened in revert.Reopened)
            {
                await _broadcast.BroadcastTrackerUpdateAsync(
                    TrackerRealtimeAction.Reopen, TrackerInstanceDto.From(reopened, UtcNow));
                if (reopened.StartTreatmentId is { } startedBy && deletedIds.Contains(startedBy))
                    pending.Push(reopened);
            }
        }

        await ReopenEndedAsync(deletedIds, ct);
    }

    /// <summary>
    /// A deleted end never happened either, so the run it completed resumes, unless a later run has
    /// started since (<see cref="ITrackerRepository.ReopenCompletedByAsync"/>). Runs after the start
    /// reverts, which have already reopened what a deleted start had replaced.
    /// </summary>
    private async Task ReopenEndedAsync(IReadOnlyCollection<string> deletedIds, CancellationToken ct)
    {
        foreach (var definitionId in await _trackerRepository.GetDefinitionsCompletedByAsync(deletedIds, ct))
        {
            var reopened = await _trackerRepository.ExecuteUnderDefinitionLockAsync(
                definitionId,
                (tx, token) => tx.ReopenCompletedByAsync(definitionId, deletedIds, token),
                async (tx, attempt, token) =>
                {
                    foreach (var instance in attempt)
                        if (await tx.GetInstanceByIdAsync(instance.Id, token) is not { CompletedAt: null })
                            return false;
                    return true;
                },
                ct);

            foreach (var instance in reopened)
            {
                _logger.LogInformation(
                    "Reopened tracker instance {InstanceId} for {DefinitionName}: the event that ended it was deleted",
                    instance.Id,
                    instance.Definition.Name);
                await _broadcast.BroadcastTrackerUpdateAsync(
                    TrackerRealtimeAction.Reopen, TrackerInstanceDto.From(instance, UtcNow));
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
        TrackerDefinition definition,
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
            _time,
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
    private static bool MatchesNotesFilter(TrackerDefinition definition, DeviceEvent deviceEvent)
    {
        if (string.IsNullOrWhiteSpace(definition.TriggerNotesContains))
            return true;

        return deviceEvent.Notes is not null
            && deviceEvent.Notes.Contains(definition.TriggerNotesContains, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a definition's trigger list, which holds Nightscout event-type names, to device event
    /// types: the names in <see cref="TrackerEventTypes.TriggerableEventTypes"/>, through
    /// <see cref="TrackerEventTypes.CanonicalTrigger"/> as the definition DTO reads them. Any other
    /// name is ignored.
    /// </summary>
    internal static HashSet<DeviceEventType> ParseTriggerEventTypes(IEnumerable<string> eventTypes) =>
        ParseEventTypes(eventTypes, TrackerEventTypes.CanonicalTrigger);

    /// <summary>
    /// As <see cref="ParseTriggerEventTypes"/>, for a definition's end list against
    /// <see cref="TrackerEventTypes.EndingEventTypes"/>.
    /// </summary>
    internal static HashSet<DeviceEventType> ParseEndEventTypes(IEnumerable<string> eventTypes) =>
        ParseEventTypes(eventTypes, TrackerEventTypes.CanonicalEnd);

    private static HashSet<DeviceEventType> ParseEventTypes(IEnumerable<string> eventTypes, Func<string, string?> canonicalise) =>
        [.. eventTypes
            .Select(canonicalise)
            .OfType<string>()
            .Select(name => TreatmentTypeMaps.DeviceEventTypeMap.TryGetValue(name, out var parsed)
                ? parsed
                : (DeviceEventType?)null)
            .OfType<DeviceEventType>()];
}
