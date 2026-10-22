using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Abstractions;

/// <summary>
/// Repository port for Tracker operations (definitions, instances, presets)
/// </summary>
public interface ITrackerRepository
{
    // Definitions

    /// <summary>
    /// Gets all tracker definitions accessible to a user
    /// </summary>
    Task<List<TrackerDefinitionEntity>> GetDefinitionsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all tracker definitions in the system
    /// </summary>
    Task<List<TrackerDefinitionEntity>> GetAllDefinitionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tracker definitions for a user filtered by category
    /// </summary>
    Task<List<TrackerDefinitionEntity>> GetDefinitionsByCategoryAsync(
        string userId,
        TrackerCategory category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tracker definitions marked as favorites by a user
    /// </summary>
    Task<TrackerDefinitionEntity[]> GetFavoriteDefinitionsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific tracker definition by its identifier
    /// </summary>
    Task<TrackerDefinitionEntity?> GetDefinitionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tracker definition
    /// </summary>
    Task<TrackerDefinitionEntity> CreateDefinitionAsync(
        TrackerDefinitionEntity definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing tracker definition
    /// </summary>
    Task<TrackerDefinitionEntity?> UpdateDefinitionAsync(
        Guid id,
        TrackerDefinitionEntity updated,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tracker definition
    /// </summary>
    Task<bool> DeleteDefinitionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the notification thresholds for a tracker definition
    /// </summary>
    Task UpdateNotificationThresholdsAsync(
        Guid definitionId,
        List<TrackerNotificationThresholdEntity> thresholds,
        CancellationToken cancellationToken = default);

    // Instances

    /// <summary>
    /// Gets all active tracker instances, optionally filtered by user
    /// </summary>
    Task<TrackerInstanceEntity[]> GetActiveInstancesAsync(
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active instances for a specific tracker definition
    /// </summary>
    Task<List<TrackerInstanceEntity>> GetActiveInstancesForDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets, per tracker definition, the reference timestamp of the instance active at
    /// <paramref name="at"/>: start time for Duration mode, scheduled time for Event mode.
    /// When several instances of a definition are active, the most recently started wins.
    /// Definitions with no active instance are absent from the result. Feeds the
    /// <c>tracker_age</c> alert condition (live evaluation passes now; replay passes the tick).
    /// </summary>
    Task<Dictionary<Guid, DateTime>> GetActiveTrackerReferencesAsync(
        IReadOnlySet<Guid> definitionIds,
        DateTime at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets completed tracker instances, optionally filtered by user, with an optional limit
    /// </summary>
    Task<TrackerInstanceEntity[]> GetCompletedInstancesAsync(
        string? userId,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets upcoming scheduled tracker instances for a user within a date range
    /// </summary>
    Task<TrackerInstanceEntity[]> GetUpcomingInstancesAsync(
        string? userId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific tracker instance by its identifier
    /// </summary>
    Task<TrackerInstanceEntity?> GetInstanceByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a new instance of a tracker definition
    /// </summary>
    Task<TrackerInstanceEntity> StartInstanceAsync(
        Guid definitionId,
        string userId,
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
    Task<TrackerInstanceEntity?> CompleteInstanceAsync(
        Guid instanceId,
        CompletionReason reason,
        string? completionNotes = null,
        string? completeTreatmentId = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The running instances, of any definition, whose <see cref="TrackerInstanceEntity.StartTreatmentId"/>
    /// is one of <paramref name="startTreatmentIds"/>.
    /// </summary>
    Task<List<TrackerInstanceEntity>> GetRunningStartedByAsync(
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
    /// carry the same id as <see cref="TrackerInstanceEntity.CompleteTreatmentId"/>. A run that has
    /// since been replaced is history and is left as it is. Call it under the definition's lock
    /// (<see cref="ExecuteUnderDefinitionLockAsync{T}"/>).
    /// </summary>
    /// <returns>What was undone, or null when no running instance was started by the id.</returns>
    Task<TrackerStartRevert?> RevertStartAsync(
        Guid definitionId,
        string startTreatmentId,
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

    /// <summary>
    /// Acknowledges a tracker instance notification and snoozes further alerts
    /// </summary>
    Task<bool> AckInstanceAsync(
        Guid instanceId,
        int snoozeMins,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tracker instance
    /// </summary>
    Task<bool> DeleteInstanceAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Presets

    /// <summary>
    /// Gets all tracker presets defined by a user
    /// </summary>
    Task<TrackerPresetEntity[]> GetPresetsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific tracker preset by its identifier
    /// </summary>
    Task<TrackerPresetEntity?> GetPresetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tracker preset
    /// </summary>
    Task<TrackerPresetEntity> CreatePresetAsync(
        TrackerPresetEntity preset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tracker preset
    /// </summary>
    Task<bool> DeletePresetAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>A run undone by <see cref="ITrackerRepository.RevertStartAsync"/>.</summary>
/// <param name="Removed">The deleted run.</param>
/// <param name="Reopened">The runs it had replaced, running again.</param>
public sealed record TrackerStartRevert(TrackerInstanceEntity Removed, IReadOnlyList<TrackerInstanceEntity> Reopened);
