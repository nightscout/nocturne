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
    /// Gets completed tracker instances for a user, with an optional limit
    /// </summary>
    Task<TrackerInstanceEntity[]> GetCompletedInstancesAsync(
        string userId,
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
    /// Completes an active tracker instance with a specified reason
    /// </summary>
    Task<TrackerInstanceEntity?> CompleteInstanceAsync(
        Guid instanceId,
        CompletionReason reason,
        string? completionNotes = null,
        string? completeTreatmentId = null,
        DateTime? completedAt = null,
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
    /// Applies a tracker preset, creating a new instance for a user
    /// </summary>
    Task<TrackerInstanceEntity?> ApplyPresetAsync(
        Guid presetId,
        string userId,
        string? overrideNotes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tracker preset
    /// </summary>
    Task<bool> DeletePresetAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
