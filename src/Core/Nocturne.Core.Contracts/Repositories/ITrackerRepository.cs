using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Stores tracker definitions with their notification thresholds, their runs and their presets.
/// </summary>
/// <remarks>
/// Trackers are the tenant's, not a member's: every read returns the whole tenant's rows, scoped by
/// the tenant filter and row level security alone. A run is read with its definition and that
/// definition's thresholds; a preset with its definition alone.
/// </remarks>
public interface ITrackerRepository
{
    /// <summary>The tenant's definitions, by name.</summary>
    Task<IReadOnlyList<TrackerDefinition>> GetAllDefinitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The tenant's definitions in <paramref name="category"/>, by name.</summary>
    Task<IReadOnlyList<TrackerDefinition>> GetDefinitionsByCategoryAsync(
        TrackerCategory category,
        CancellationToken cancellationToken = default);

    Task<TrackerDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="definition"/> with its thresholds, under a new id and stamped created now.
    /// </summary>
    /// <returns>The definition as stored.</returns>
    Task<TrackerDefinition> CreateDefinitionAsync(
        TrackerDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets every field of the stored definition with <paramref name="definition"/>'s id to its value
    /// and stamps it updated. The thresholds are left as they are
    /// (<see cref="UpdateNotificationThresholdsAsync"/> replaces them).
    /// </summary>
    /// <returns>The definition as stored, or null when there is none.</returns>
    Task<TrackerDefinition?> UpdateDefinitionAsync(
        TrackerDefinition definition,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every threshold of <paramref name="definitionId"/> with <paramref name="thresholds"/>,
    /// each stored under a new id.
    /// </summary>
    Task UpdateNotificationThresholdsAsync(
        Guid definitionId,
        IReadOnlyList<TrackerNotificationThreshold> thresholds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="alertRuleId"/> as the managed rule that delivers threshold
    /// <paramref name="thresholdId"/>. On a repository over a caller's unit of work it is staged on
    /// the unit and saved with the unit's next save.
    /// </summary>
    Task LinkThresholdRuleAsync(Guid thresholdId, Guid alertRuleId, CancellationToken cancellationToken = default);

    /// <summary>The definitions with a threshold that no managed rule delivers.</summary>
    Task<IReadOnlyList<Guid>> GetDefinitionsWithUnlinkedThresholdsAsync(CancellationToken cancellationToken = default);

    /// <summary>The running instances, newest first.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>The running instances of <paramref name="definitionId"/>, newest first.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesForDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets, per tracker definition, the instance active at <paramref name="at"/>: its reference
    /// time (start for Duration mode, scheduled time for Event mode) and when it was recorded.
    /// When several instances of a definition are active, the most recently started wins.
    /// Definitions with no active instance are absent from the result. Feeds the
    /// <c>tracker_age</c> alert condition (live evaluation passes now; replay passes the tick).
    /// </summary>
    Task<Dictionary<Guid, ActiveTrackerReference>> GetActiveTrackerReferencesAsync(
        IReadOnlySet<Guid> definitionIds,
        DateTime at,
        CancellationToken cancellationToken = default);

    /// <summary>The most recently completed instances, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetCompletedInstancesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>The running instances whose lifespan ends between <paramref name="from"/> and <paramref name="to"/>.</summary>
    Task<IReadOnlyList<TrackerInstance>> GetUpcomingInstancesAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The runs that started before <paramref name="to"/> and were running after <paramref name="from"/>,
    /// earliest first, up to <paramref name="limit"/>. Their definitions are read without thresholds.
    /// </summary>
    Task<IReadOnlyList<TrackerInstance>> GetRunsOverlappingAsync(
        DateTime from,
        DateTime to,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Whether a run started before <paramref name="to"/> and was running at or after <paramref name="from"/>.</summary>
    Task<bool> AnyRunInAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    Task<TrackerInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Starts a run of <paramref name="definitionId"/>, recorded now and started now unless <paramref name="startedAt"/> says otherwise.</summary>
    Task<TrackerInstance> StartInstanceAsync(
        Guid definitionId,
        string createdBy,
        string? startNotes = null,
        string? startTreatmentId = null,
        DateTime? startedAt = null,
        DateTime? scheduledAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a running tracker instance with a specified reason, in one conditional write.
    /// Returns null, changing nothing, when the instance is not found or is already completed, so
    /// of two writers completing one instance exactly one gets it back.
    /// </summary>
    Task<TrackerInstance?> CompleteInstanceAsync(
        Guid instanceId,
        CompletionReason reason,
        string? completionNotes = null,
        string? completeTreatmentId = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The running instances, of any definition, whose <see cref="TrackerInstance.StartTreatmentId"/>
    /// is one of <paramref name="startTreatmentIds"/>.
    /// </summary>
    Task<IReadOnlyList<TrackerInstance>> GetRunningStartedByAsync(
        IReadOnlyCollection<string> startTreatmentIds,
        CancellationToken cancellationToken = default);

    /// <summary>Whether a run of <paramref name="definitionId"/> was completed after <paramref name="at"/>.</summary>
    Task<bool> HasRunEndedAfterAsync(
        Guid definitionId,
        DateTime at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes the run of <paramref name="definitionId"/> that <paramref name="startTreatmentId"/>
    /// started, while that run is still going: deletes it and reopens the runs it replaced, which
    /// carry the same id as <see cref="TrackerInstance.CompleteTreatmentId"/>. A run that has
    /// since been replaced is history and is left as it is. Call it under the definition's lock
    /// (<see cref="ExecuteUnderDefinitionLockAsync{T}"/>).
    /// </summary>
    /// <returns>What was undone, or null when no running instance was started by the id.</returns>
    Task<TrackerStartRevert?> RevertStartAsync(
        Guid definitionId,
        string startTreatmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The definitions with a run whose <see cref="TrackerInstance.CompleteTreatmentId"/> is one
    /// of <paramref name="completeTreatmentIds"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDefinitionsCompletedByAsync(
        IReadOnlyCollection<string> completeTreatmentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes an end: reopens the runs of <paramref name="definitionId"/> that one of
    /// <paramref name="completeTreatmentIds"/> completed, except a run a later run of the definition
    /// has followed, which makes it history. Call it under the definition's lock
    /// (<see cref="ExecuteUnderDefinitionLockAsync{T}"/>).
    /// </summary>
    /// <returns>The reopened runs; empty when nothing was reopened.</returns>
    Task<IReadOnlyList<TrackerInstance>> ReopenCompletedByAsync(
        Guid definitionId,
        IReadOnlyCollection<string> completeTreatmentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction holding the definition's lock, so that
    /// writers succeeding one tracker's run take turns.
    /// </summary>
    /// <remarks>
    /// <paramref name="work"/> can run more than once when the store retries a transient failure.
    /// <paramref name="verifySucceeded"/> judges, after a commit that reported failure, whether the
    /// result the attempt returned landed anyway.
    /// </remarks>
    Task<T> ExecuteUnderDefinitionLockAsync<T>(
        Guid definitionId,
        Func<ITrackerRepository, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a run's notification and snoozes it for <paramref name="snoozeMins"/>.</summary>
    Task<bool> AckInstanceAsync(
        Guid instanceId,
        int snoozeMins,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteInstanceAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every run of <paramref name="definitionId"/>, for a re-seed that rebuilds them.
    /// </summary>
    Task PurgeInstancesAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's presets, by name.</summary>
    Task<IReadOnlyList<TrackerPreset>> GetPresetsAsync(CancellationToken cancellationToken = default);

    Task<TrackerPreset?> GetPresetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="preset"/> under a new id, stamped created now.</summary>
    /// <returns>The preset as stored, read without its definition.</returns>
    Task<TrackerPreset> CreatePresetAsync(TrackerPreset preset, CancellationToken cancellationToken = default);

    Task<bool> DeletePresetAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>A run undone by <see cref="ITrackerRepository.RevertStartAsync"/>.</summary>
/// <param name="Removed">The deleted run.</param>
/// <param name="Reopened">The runs it had replaced, running again.</param>
public sealed record TrackerStartRevert(TrackerInstance Removed, IReadOnlyList<TrackerInstance> Reopened);
