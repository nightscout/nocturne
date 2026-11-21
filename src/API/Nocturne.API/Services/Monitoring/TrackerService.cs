using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Reads and writes a tenant's trackers. A write is held to the rules a definition keeps
/// (<see cref="TrackerDefinition"/>), stored, and then followed by what a tracker write owes the
/// rest of the system: the definition's managed alert rules, the realtime broadcast, the device
/// event a start or completion records, and the alert acknowledgement a tracker acknowledgement
/// stands for.
/// </summary>
/// <remarks>
/// Every step after the store runs on <see cref="CancellationToken.None"/>, except the alert
/// acknowledgement: what it follows is committed, and a client that has gone must not leave a
/// definition without its rules or a run without its event.
/// </remarks>
public interface ITrackerService
{
    /// <summary>The tenant's definitions, all of them or those in <paramref name="category"/>.</summary>
    Task<IReadOnlyList<TrackerDefinition>> GetDefinitionsAsync(TrackerCategory? category, CancellationToken ct);

    Task<TrackerDefinition?> GetDefinitionAsync(Guid id, CancellationToken ct);

    /// <summary>The running instances, newest first.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesAsync(CancellationToken ct);

    /// <summary>The most recently completed instances, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetInstanceHistoryAsync(int limit, CancellationToken ct);

    /// <summary>The running instances due to end between <paramref name="from"/> and <paramref name="to"/>.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetUpcomingInstancesAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<IReadOnlyList<TrackerPreset>> GetPresetsAsync(CancellationToken ct);

    /// <summary>Creates the definition <paramref name="draft"/> describes, then syncs its rules and broadcasts it.</summary>
    /// <returns>The stored definition, or the rule the draft breaks, when nothing is stored.</returns>
    Task<TrackerDefinitionResult> CreateDefinitionAsync(TrackerDefinitionDraft draft, string userId, CancellationToken ct);

    /// <summary>
    /// Applies <paramref name="edit"/> to the definition, then syncs its rules and broadcasts it, as
    /// <see cref="CreateDefinitionAsync"/> does. Posted thresholds replace the stored ones before the
    /// definition's own fields are saved.
    /// </summary>
    /// <returns>Null when there is no such definition; else the stored definition, or the rule the edit breaks, when nothing is stored.</returns>
    Task<TrackerDefinitionResult?> UpdateDefinitionAsync(Guid id, TrackerDefinitionEdit edit, CancellationToken ct);

    /// <summary>
    /// Deletes the definition, then its managed rules, then broadcasts it. If the delete fails the
    /// tracker keeps its rules; rules left by an interrupted cleanup fail closed, since a deleted
    /// definition has no running instance.
    /// </summary>
    /// <returns>False when there is no such definition.</returns>
    Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Starts a run of <paramref name="definitionId"/> at <paramref name="startedAt"/>, or now, which
    /// replaces the running one (<see cref="TrackerSuccession"/>), then records the definition's
    /// start event.
    /// </summary>
    Task<TrackerStartResult> StartInstanceAsync(
        Guid definitionId,
        string userId,
        string? startNotes,
        string? startTreatmentId,
        DateTime? startedAt,
        DateTime? scheduledAt,
        CancellationToken ct);

    /// <summary>Starts a run now with the preset's definition and notes, as <see cref="StartInstanceAsync"/> does.</summary>
    /// <returns>Null when there is no such preset.</returns>
    Task<TrackerStartResult?> ApplyPresetAsync(Guid presetId, string userId, string? overrideNotes, CancellationToken ct);

    /// <summary>Completes a running instance, then records the definition's completion event and broadcasts the run.</summary>
    Task<TrackerCompletion> CompleteInstanceAsync(
        Guid id,
        CompletionReason reason,
        string? completionNotes,
        string? completeTreatmentId,
        DateTime? completedAt,
        CancellationToken ct);

    /// <summary>
    /// Records the acknowledgement and snooze on the instance, acknowledges the open excursions of the
    /// definition's managed alert rules so the alert surface agrees, then broadcasts the run.
    /// </summary>
    /// <returns>False when there is no such instance.</returns>
    Task<bool> AcknowledgeInstanceAsync(
        Guid id,
        int snoozeMins,
        string userId,
        AlertAcknowledgementAuthority authority,
        CancellationToken ct);

    /// <summary>Deletes an instance, then broadcasts it as it was.</summary>
    /// <returns>False when there is no such instance.</returns>
    Task<bool> DeleteInstanceAsync(Guid id, CancellationToken ct);

    /// <summary>Stores a preset of <paramref name="definitionId"/>.</summary>
    /// <returns>Null when there is no such definition.</returns>
    Task<TrackerPreset?> CreatePresetAsync(
        string name, Guid definitionId, string? defaultStartNotes, string userId, CancellationToken ct);

    /// <returns>False when there is no such preset.</returns>
    Task<bool> DeletePresetAsync(Guid id, CancellationToken ct);
}

/// <summary>How a start ended.</summary>
/// <param name="Definition">The definition started; null when there is none.</param>
/// <param name="Invalid">Why the start was refused, for a start the definition's mode does not allow.</param>
/// <param name="Succession">How the succession ended; null when it did not run.</param>
public sealed record TrackerStartResult(
    TrackerDefinition? Definition,
    string? Invalid = null,
    TrackerSuccessionResult? Succession = null)
{
    public TrackerInstance? Started => Succession?.Started;
}

/// <summary>How a completion ended.</summary>
public enum TrackerCompletionOutcome
{
    Completed,
    NotFound,
    AlreadyCompleted,
}

/// <param name="Completed">The completed run, when <paramref name="Outcome"/> is Completed.</param>
public sealed record TrackerCompletion(TrackerCompletionOutcome Outcome, TrackerInstance? Completed = null);

/// <inheritdoc />
public sealed class TrackerService(
    ITrackerRepository repository,
    ISignalRBroadcastService broadcast,
    ITrackerAlertRuleSyncService ruleSync,
    IAlertRepository alerts,
    ITenantAccessor tenantAccessor,
    IAlertAcknowledgementService acknowledgements,
    TrackerDeviceEventWriter deviceEvents,
    TimeProvider timeProvider,
    ILogger<TrackerService> logger) : ITrackerService
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackerDefinition>> GetDefinitionsAsync(TrackerCategory? category, CancellationToken ct) =>
        category is { } only
            ? repository.GetDefinitionsByCategoryAsync(only, ct)
            : repository.GetAllDefinitionsAsync(ct);

    /// <inheritdoc />
    public Task<TrackerDefinition?> GetDefinitionAsync(Guid id, CancellationToken ct) =>
        repository.GetDefinitionByIdAsync(id, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesAsync(CancellationToken ct) =>
        repository.GetActiveInstancesAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackerInstance>> GetInstanceHistoryAsync(int limit, CancellationToken ct) =>
        repository.GetCompletedInstancesAsync(limit, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackerInstance>> GetUpcomingInstancesAsync(DateTime from, DateTime to, CancellationToken ct) =>
        repository.GetUpcomingInstancesAsync(from, to, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackerPreset>> GetPresetsAsync(CancellationToken ct) =>
        repository.GetPresetsAsync(ct);

    /// <inheritdoc />
    public async Task<TrackerDefinitionResult> CreateDefinitionAsync(
        TrackerDefinitionDraft draft, string userId, CancellationToken ct)
    {
        var result = TrackerDefinition.Create(draft, userId, UtcNow);
        if (result.Definition is not { } definition)
            return result;

        var created = await repository.CreateDefinitionAsync(definition, ct);
        await AfterDefinitionWrittenAsync(created.Id);

        logger.LogInformation("Tracker definition {Id} created by {CreatedBy}", created.Id, userId);
        return new TrackerDefinitionResult(created);
    }

    /// <inheritdoc />
    public async Task<TrackerDefinitionResult?> UpdateDefinitionAsync(
        Guid id, TrackerDefinitionEdit edit, CancellationToken ct)
    {
        if (await repository.GetDefinitionByIdAsync(id, ct) is not { } existing)
            return null;

        var result = existing.Revise(edit);
        if (result.Definition is not { } revised)
            return result;

        if (edit.NotificationThresholds is not null)
            await repository.UpdateNotificationThresholdsAsync(id, revised.NotificationThresholds, ct);
        var updated = await repository.UpdateDefinitionAsync(revised, ct);

        // Lifespan, mode and name shift the synthesised conditions even when the thresholds did not
        // change, so the rules are synced on every update.
        await AfterDefinitionWrittenAsync(id);

        return new TrackerDefinitionResult(
            updated ?? throw new InvalidOperationException($"Tracker definition {id} was deleted while it was updated"));
    }

    /// <summary>What a stored definition write owes, in order: its managed rules, then the broadcast.</summary>
    private async Task AfterDefinitionWrittenAsync(Guid definitionId)
    {
        await ruleSync.SyncDefinitionAsync(definitionId, CancellationToken.None);
        await broadcast.BroadcastTrackerDefinitionUpdateAsync(definitionId);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct)
    {
        if (await repository.GetDefinitionByIdAsync(id, ct) is null)
            return false;

        await repository.DeleteDefinitionAsync(id, ct);
        await ruleSync.DeleteRulesForDefinitionAsync(id, CancellationToken.None);
        await broadcast.BroadcastTrackerDefinitionUpdateAsync(id);

        logger.LogInformation("Deleted tracker definition {Id}", id);
        return true;
    }

    /// <inheritdoc />
    public async Task<TrackerStartResult> StartInstanceAsync(
        Guid definitionId,
        string userId,
        string? startNotes,
        string? startTreatmentId,
        DateTime? startedAt,
        DateTime? scheduledAt,
        CancellationToken ct)
    {
        if (await repository.GetDefinitionByIdAsync(definitionId, ct) is not { } definition)
            return new TrackerStartResult(null);

        if (definition.CannotStart(scheduledAt) is { } invalid)
            return new TrackerStartResult(definition, invalid);

        var succession = await StartSuccessorAsync(
            definition, userId, startNotes, startTreatmentId, startedAt ?? UtcNow, scheduledAt, ct);
        if (succession.Started is { } instance)
            logger.LogInformation(
                "Started tracker instance {Id} for definition {DefinitionId}", instance.Id, definitionId);
        return new TrackerStartResult(definition, Succession: succession);
    }

    /// <inheritdoc />
    public async Task<TrackerStartResult?> ApplyPresetAsync(
        Guid presetId, string userId, string? overrideNotes, CancellationToken ct)
    {
        if (await repository.GetPresetByIdAsync(presetId, ct) is not { Definition: { } definition } preset)
            return null;

        var succession = await StartSuccessorAsync(
            definition, userId, overrideNotes ?? preset.DefaultStartNotes, startTreatmentId: null, UtcNow, scheduledAt: null, ct);
        if (succession.Started is { } instance)
            logger.LogInformation(
                "Applied preset {PresetId}, created instance {InstanceId}", presetId, instance.Id);
        return new TrackerStartResult(definition, Succession: succession);
    }

    /// <summary>
    /// A manual start replaces the running instance, as a device-event start does; see
    /// <see cref="TrackerSuccession"/>. A started run records the definition's start event
    /// (<see cref="TrackerDeviceEventWriter"/>).
    /// </summary>
    private async Task<TrackerSuccessionResult> StartSuccessorAsync(
        TrackerDefinition definition,
        string userId,
        string? startNotes,
        string? startTreatmentId,
        DateTime startedAt,
        DateTime? scheduledAt,
        CancellationToken ct)
    {
        var succession = await TrackerSuccession.StartAsync(
            repository,
            broadcast,
            timeProvider,
            logger,
            definition,
            startedAt,
            completionNotes: null,
            completeTreatmentId: null,
            (tx, token) => tx.StartInstanceAsync(
                definition.Id,
                userId,
                startNotes,
                startTreatmentId,
                startedAt,
                scheduledAt,
                token
            ),
            ct
        );

        if (succession.Started is { } started)
            await deviceEvents.WriteAsync(
                definition.StartEventType,
                started.StartedAt,
                startNotes,
                $"{started.Id}:start",
                CancellationToken.None);

        return succession;
    }

    /// <inheritdoc />
    public async Task<TrackerCompletion> CompleteInstanceAsync(
        Guid id,
        CompletionReason reason,
        string? completionNotes,
        string? completeTreatmentId,
        DateTime? completedAt,
        CancellationToken ct)
    {
        if (await repository.GetInstanceByIdAsync(id, ct) is not { } existing)
            return new TrackerCompletion(TrackerCompletionOutcome.NotFound);
        if (existing.CompletedAt != null)
            return new TrackerCompletion(TrackerCompletionOutcome.AlreadyCompleted);

        var completed = await repository.CompleteInstanceAsync(
            id, reason, completionNotes, completeTreatmentId, completedAt, ct);
        if (completed is null)
            return new TrackerCompletion(TrackerCompletionOutcome.AlreadyCompleted);

        if (completed.CompletedAt is { } at)
            await deviceEvents.WriteAsync(
                existing.Definition.CompletionEventType,
                at,
                completed.CompletionNotes,
                $"{completed.Id}:completion",
                CancellationToken.None);

        logger.LogInformation("Completed tracker instance {Id} with reason {Reason}", id, reason);

        await broadcast.BroadcastTrackerUpdateAsync(TrackerRealtimeAction.Complete, TrackerInstanceDto.From(completed, UtcNow));
        return new TrackerCompletion(TrackerCompletionOutcome.Completed, completed);
    }

    /// <inheritdoc />
    public async Task<bool> AcknowledgeInstanceAsync(
        Guid id,
        int snoozeMins,
        string userId,
        AlertAcknowledgementAuthority authority,
        CancellationToken ct)
    {
        if (await repository.GetInstanceByIdAsync(id, ct) is not { } existing)
            return false;

        await repository.AckInstanceAsync(id, snoozeMins, ct);

        var excursions = await alerts.GetUnacknowledgedExcursionIdsAsync(
            TrackerAlertRuleSyncService.ManagedByTag(existing.DefinitionId), ct);
        foreach (var excursionId in excursions)
            await acknowledgements.AcknowledgeExcursionAsync(
                tenantAccessor.TenantId, excursionId, userId, authority, broadcast: true, ct);

        if (await repository.GetInstanceByIdAsync(id, ct) is { } updated)
            await broadcast.BroadcastTrackerUpdateAsync(TrackerRealtimeAction.Ack, TrackerInstanceDto.From(updated, UtcNow));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteInstanceAsync(Guid id, CancellationToken ct)
    {
        if (await repository.GetInstanceByIdAsync(id, ct) is not { } existing)
            return false;

        var dto = TrackerInstanceDto.From(existing, UtcNow);
        await repository.DeleteInstanceAsync(id, ct);
        await broadcast.BroadcastTrackerUpdateAsync(TrackerRealtimeAction.Delete, dto);
        return true;
    }

    /// <inheritdoc />
    public async Task<TrackerPreset?> CreatePresetAsync(
        string name, Guid definitionId, string? defaultStartNotes, string userId, CancellationToken ct)
    {
        if (await repository.GetDefinitionByIdAsync(definitionId, ct) is null)
            return null;

        var created = await repository.CreatePresetAsync(
            new TrackerPreset
            {
                CreatedBy = userId,
                Name = name,
                DefinitionId = definitionId,
                DefaultStartNotes = defaultStartNotes,
            },
            ct);

        logger.LogInformation("Tracker preset {Id} created by {CreatedBy}", created.Id, userId);
        return created;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePresetAsync(Guid id, CancellationToken ct)
    {
        if (await repository.GetPresetByIdAsync(id, ct) is null)
            return false;

        await repository.DeletePresetAsync(id, ct);
        return true;
    }
}
