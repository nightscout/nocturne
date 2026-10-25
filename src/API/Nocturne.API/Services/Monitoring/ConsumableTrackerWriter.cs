using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>A consumable tracker <see cref="ConsumableTrackerWriter"/> wrote, not yet announced.</summary>
/// <param name="Definition">The definition as written in the transaction.</param>
/// <param name="Run">Its first run, or null when no event started one.</param>
/// <param name="StartEvent">The device event <paramref name="Run"/> started at.</param>
public sealed record CreatedConsumableTracker(
    TrackerDefinitionEntity Definition,
    ConsumableTrackerSpec Spec,
    TrackerInstanceEntity? Run,
    DeviceEvent? StartEvent);

/// <summary>
/// Creates a tracker for a piece of consumable hardware, as the legacy conversion, the device
/// provisioner and the setup hub all do: the definition, its managed alert rules when it has a
/// ladder, and a first run started at the latest device event that restarts it, in the caller's
/// transaction under the consumable lock
/// (<see cref="ITrackerRepository.ExecuteUnderConsumableLockAsync{T}"/>), then announced after the
/// commit.
/// </summary>
public sealed class ConsumableTrackerWriter(
    IDeviceEventRepository deviceEvents,
    ITrackerAlertRuleSyncService ruleSync,
    IDeviceEventReactor deviceEventReactor,
    ISignalRBroadcastService broadcast,
    TimeProvider timeProvider,
    ILogger<ConsumableTrackerWriter> logger)
{
    /// <summary>
    /// Writes the tracker on <paramref name="tx"/> and <paramref name="unit"/>. The run starts at
    /// the latest event that restarts <paramref name="spec"/>, through <see cref="TrackerSuccession"/>,
    /// whose definition lock is taken inside the consumable lock; an event older than
    /// <see cref="LegacyFallbackRules.StaleAfterLifespans"/> lifespans starts none.
    /// </summary>
    /// <param name="withAlerts">Whether the tracker gets its <see cref="ConsumableTrackerSpecs.Thresholds"/>
    /// and their managed rules.</param>
    public async Task<CreatedConsumableTracker> CreateAsync(
        ITrackerRepository tx,
        DbUnitOfWork unit,
        ConsumableTrackerSpec spec,
        string createdBy,
        bool withAlerts,
        CancellationToken ct)
    {
        var definition = await tx.CreateDefinitionAsync(
            ConsumableTrackerSpecs.ToDefinition(spec, createdBy, withAlerts), ct);
        if (withAlerts)
            await ruleSync.CreateRulesAsync(unit, definition.Id, ct);

        if (await LatestEventAsync(spec, ct) is not { } latest)
            return new CreatedConsumableTracker(definition, spec, null, null);

        var startTreatmentId = latest.Id.ToString();
        var succession = await TrackerSuccession.StartAsync(
            tx,
            broadcast: null,
            logger,
            definition,
            latest.Timestamp,
            completionNotes: null,
            completeTreatmentId: null,
            (repository, token) => repository.StartInstanceAsync(
                definition.Id,
                createdBy,
                startTreatmentId: startTreatmentId,
                startedAt: latest.Timestamp,
                cancellationToken: token),
            ct,
            alreadyStarted: running => running.Any(i => i.StartTreatmentId == startTreatmentId));
        return new CreatedConsumableTracker(definition, spec, succession.Started, latest);
    }

    /// <summary>
    /// Whether <paramref name="created"/> landed, judged on <paramref name="tx"/> after a commit that
    /// reported failure: its definition and, when one started, its run exist. Both ids are new, so
    /// finding them proves the commit.
    /// </summary>
    public static async Task<bool> LandedAsync(
        ITrackerRepository tx, CreatedConsumableTracker created, CancellationToken ct) =>
        await tx.GetDefinitionByIdAsync(created.Definition.Id, ct) is not null
        && (created.Run is null || await tx.GetInstanceByIdAsync(created.Run.Id, ct) is not null);

    /// <summary>
    /// Announces a committed <paramref name="created"/> tracker and its run, then offers the latest
    /// restarting event again when it is newer than the run's start: one that arrived while the
    /// transaction was open found no committed tracker to advance.
    /// </summary>
    /// <returns>The definition and its running instance after the catch-up.</returns>
    public async Task<(TrackerDefinitionEntity Definition, TrackerInstanceEntity? Run)?> PublishAsync(
        ITrackerRepository trackers, CreatedConsumableTracker created, CancellationToken ct)
    {
        await broadcast.BroadcastTrackerDefinitionUpdateAsync(created.Definition.Id);
        if (created.Run is not null && await trackers.GetInstanceByIdAsync(created.Run.Id, ct) is { } started)
            await broadcast.BroadcastTrackerUpdateAsync(TrackerRealtimeAction.Create, TrackerInstanceDto.FromEntity(started));

        if (await LatestEventAsync(created.Spec, ct) is { } latest
            && latest.Id != created.StartEvent?.Id
            && (created.StartEvent is null || latest.Timestamp > created.StartEvent.Timestamp))
        {
            await deviceEventReactor.OnCreatedAsync([latest], ct);
        }

        if (await trackers.GetDefinitionByIdAsync(created.Definition.Id, ct) is not { } definition)
            return null;
        var run = (await trackers.GetActiveInstancesForDefinitionAsync(created.Definition.Id, ct)).FirstOrDefault();
        return (definition, run);
    }

    private async Task<DeviceEvent?> LatestEventAsync(ConsumableTrackerSpec spec, CancellationToken ct)
    {
        var eventTypes = ConsumableTrackerSpecs.TriggerEventTypes(spec).ToArray();
        if (eventTypes.Length == 0)
            return null;
        var latest = await deviceEvents.GetLatestByEventTypesAsync(eventTypes, patientDeviceId: null, ct);
        return latest is not null
            && !LegacyFallbackRules.IsStale(spec, latest.Timestamp, timeProvider.GetUtcNow().UtcDateTime)
            ? latest
            : null;
    }
}
