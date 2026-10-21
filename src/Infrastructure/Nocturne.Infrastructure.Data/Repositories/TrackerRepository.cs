using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Tracker operations (definitions, instances, presets). Each call runs
/// on its own context, or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
public class TrackerRepository : ITrackerRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context.</summary>
    public TrackerRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>A repository whose calls run on <paramref name="unit"/>, which the caller owns.</summary>
    public TrackerRepository(DbUnitOfWork unit)
    {
        _unit = unit;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    #region Definitions

    /// <summary>
    /// Get all tracker definitions for a user
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of tracker definitions visible to the user.</returns>
    public virtual async Task<List<TrackerDefinitionEntity>> GetDefinitionsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .Where(d => d.UserId == userId || d.Visibility == TrackerVisibility.Public)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get all tracker definitions (for anonymous/public access filtering)
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of all tracker definitions.</returns>
    public virtual async Task<List<TrackerDefinitionEntity>> GetAllDefinitionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get tracker definitions by category
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="category">The category to filter by.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of tracker definitions in the specified category.</returns>
    public virtual async Task<List<TrackerDefinitionEntity>> GetDefinitionsByCategoryAsync(
        string userId,
        TrackerCategory category,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .Where(d => (d.UserId == userId || d.Visibility == TrackerVisibility.Public) && d.Category == category)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get favorite tracker definitions for quick-add
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An array of favorite tracker definitions.</returns>
    public virtual async Task<TrackerDefinitionEntity[]> GetFavoriteDefinitionsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Where(d => d.UserId == userId && d.IsFavorite)
            .OrderBy(d => d.Name)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get a specific tracker definition by ID
    /// </summary>
    /// <param name="id">The definition unique identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracker definition, or null if not found.</returns>
    public virtual async Task<TrackerDefinitionEntity?> GetDefinitionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    /// <summary>
    /// Create a new tracker definition
    /// </summary>
    /// <param name="definition">The definition entity to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created tracker definition.</returns>
    public virtual async Task<TrackerDefinitionEntity> CreateDefinitionAsync(
        TrackerDefinitionEntity definition,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        definition.Id = Guid.CreateVersion7();
        definition.CreatedAt = DateTime.UtcNow;

        db.TrackerDefinitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);

        return definition;
    }

    /// <summary>
    /// Update an existing tracker definition
    /// </summary>
    /// <param name="id">The unique identifier of the definition to update.</param>
    /// <param name="updated">The updated definition data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated tracker definition, or null if not found.</returns>
    public virtual async Task<TrackerDefinitionEntity?> UpdateDefinitionAsync(
        Guid id,
        TrackerDefinitionEntity updated,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var existing = await db.TrackerDefinitions.FirstOrDefaultAsync(
            d => d.Id == id,
            cancellationToken
        );

        if (existing == null)
            return null;

        existing.Name = updated.Name;
        existing.Description = updated.Description;
        existing.Category = updated.Category;
        existing.Icon = updated.Icon;
        existing.TriggerEventTypes = updated.TriggerEventTypes;
        existing.TriggerNotesContains = updated.TriggerNotesContains;
        existing.LifespanHours = updated.LifespanHours;
        existing.IsFavorite = updated.IsFavorite;
        existing.DashboardVisibility = updated.DashboardVisibility;
        existing.Visibility = updated.Visibility;
        existing.StartEventType = updated.StartEventType;
        existing.CompletionEventType = updated.CompletionEventType;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        // Reload thresholds to ensure we return the complete object
        await db.Entry(existing).Collection(d => d.NotificationThresholds).LoadAsync(cancellationToken);

        return existing;
    }

    /// <summary>
    /// Delete a tracker definition
    /// </summary>
    /// <param name="id">The unique identifier of the definition to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if deleted, otherwise false.</returns>
    public virtual async Task<bool> DeleteDefinitionAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var definition = await db.TrackerDefinitions.FirstOrDefaultAsync(
            d => d.Id == id,
            cancellationToken
        );

        if (definition == null)
            return false;

        db.TrackerDefinitions.Remove(definition);
        var result = await db.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Update notification thresholds for a definition (replaces all existing)
    /// </summary>
    /// <param name="definitionId">The tracker definition unique identifier.</param>
    /// <param name="thresholds">The new collection of thresholds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public virtual async Task UpdateNotificationThresholdsAsync(
        Guid definitionId,
        List<TrackerNotificationThresholdEntity> thresholds,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        // Remove existing thresholds
        var existing = await db
            .TrackerNotificationThresholds.Where(t => t.TrackerDefinitionId == definitionId)
            .ToListAsync(cancellationToken);

        db.TrackerNotificationThresholds.RemoveRange(existing);

        // Add new thresholds
        foreach (var threshold in thresholds)
        {
            threshold.Id = Guid.CreateVersion7();
            threshold.TrackerDefinitionId = definitionId;
            db.TrackerNotificationThresholds.Add(threshold);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    #endregion

    #region Instances

    /// <summary>
    /// Get active (not completed) tracker instances for a user
    /// </summary>
    /// <param name="userId">Optional user ID filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An array of active tracker instances.</returns>
    public virtual async Task<TrackerInstanceEntity[]> GetActiveInstancesAsync(
        string? userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => ((userId != null && i.UserId == userId) || i.Definition.Visibility == TrackerVisibility.Public) && i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get active (not completed) tracker instances for a specific definition
    /// </summary>
    /// <param name="definitionId">The tracker definition unique identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of active instances for the definition.</returns>
    public virtual async Task<List<TrackerInstanceEntity>> GetActiveInstancesForDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => i.DefinitionId == definitionId && i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<Dictionary<Guid, DateTime>> GetActiveTrackerReferencesAsync(
        IReadOnlySet<Guid> definitionIds,
        DateTime at,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        if (definitionIds.Count == 0)
            return new Dictionary<Guid, DateTime>();

        var ids = definitionIds.ToList();
        var instances = await db
            .TrackerInstances.AsNoTracking()
            .Where(i => ids.Contains(i.DefinitionId)
                && i.StartedAt <= at
                && (i.CompletedAt == null || i.CompletedAt > at))
            .OrderByDescending(i => i.StartedAt)
            .Select(i => new
            {
                i.DefinitionId,
                i.StartedAt,
                i.ScheduledAt,
                i.Definition!.Mode,
            })
            .ToListAsync(cancellationToken);

        var references = new Dictionary<Guid, DateTime>();
        foreach (var instance in instances)
        {
            // Newest-first ordering: the first instance seen per definition wins.
            if (references.ContainsKey(instance.DefinitionId))
                continue;

            var reference = instance.Mode == TrackerMode.Event
                ? instance.ScheduledAt
                : instance.StartedAt;
            if (reference is { } referenceAt)
                references[instance.DefinitionId] = referenceAt;
        }

        return references;
    }

    /// <summary>
    /// Get completed tracker instances for history
    /// </summary>
    /// <param name="userId">Optional user ID filter.</param>
    /// <param name="limit">The maximum number of instances to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An array of completed tracker instances.</returns>
    public virtual async Task<TrackerInstanceEntity[]> GetCompletedInstancesAsync(
        string? userId,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => ((userId != null && i.UserId == userId) || i.Definition.Visibility == TrackerVisibility.Public) && i.CompletedAt != null)
            .OrderByDescending(i => i.CompletedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get upcoming instance expirations for calendar display
    /// </summary>
    /// <param name="userId">Optional user ID filter.</param>
    /// <param name="from">Start of the date range.</param>
    /// <param name="to">End of the date range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An array of instances expiring within the range.</returns>
    public virtual async Task<TrackerInstanceEntity[]> GetUpcomingInstancesAsync(
        string? userId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        // Get active instances with lifespan defined
        var instances = await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i =>
                ((userId != null && i.UserId == userId) || i.Definition.Visibility == TrackerVisibility.Public) && i.CompletedAt == null && i.Definition.LifespanHours != null
            )
            .ToArrayAsync(cancellationToken);

        // Filter by expected end date (calculated in memory)
        return instances
            .Where(i =>
            {
                var expectedEnd = i.StartedAt.AddHours(i.Definition.LifespanHours!.Value);
                return expectedEnd >= from && expectedEnd <= to;
            })
            .ToArray();
    }

    /// <summary>
    /// Get a specific tracker instance by ID
    /// </summary>
    /// <param name="id">The unique identifier of the instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracker instance, or null if not found.</returns>
    public virtual async Task<TrackerInstanceEntity?> GetInstanceByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return await InstanceByIdAsync(lease.Context, id, cancellationToken);
    }

    private static Task<TrackerInstanceEntity?> InstanceByIdAsync(
        NocturneDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    /// <summary>
    /// Start a new tracker instance
    /// </summary>
    /// <param name="definitionId">The identifier of the definition to instantiate.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="startNotes">Optional initial notes.</param>
    /// <param name="startTreatmentId">Optional starting treatment ID.</param>
    /// <param name="startedAt">Optional start timestamp.</param>
    /// <param name="scheduledAt">Optional scheduled timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The newly started tracker instance.</returns>
    public virtual async Task<TrackerInstanceEntity> StartInstanceAsync(
        Guid definitionId,
        string userId,
        string? startNotes = null,
        string? startTreatmentId = null,
        DateTime? startedAt = null,
        DateTime? scheduledAt = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            DefinitionId = definitionId,
            StartedAt = startedAt ?? DateTime.UtcNow,
            StartNotes = startNotes,
            StartTreatmentId = startTreatmentId,
            ScheduledAt = scheduledAt,
        };

        db.TrackerInstances.Add(instance);
        await db.SaveChangesAsync(cancellationToken);

        await db.Entry(instance).Reference(i => i.Definition).LoadAsync(cancellationToken);
        await db.Entry(instance.Definition).Collection(d => d.NotificationThresholds).LoadAsync(cancellationToken);

        return instance;
    }

    /// <inheritdoc />
    public virtual async Task<TrackerInstanceEntity?> CompleteInstanceAsync(
        Guid instanceId,
        CompletionReason reason,
        string? completionNotes = null,
        string? completeTreatmentId = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var at = completedAt ?? DateTime.UtcNow;
        var updated = await db
            .TrackerInstances.Where(i => i.Id == instanceId && i.CompletedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(i => i.CompletedAt, at)
                    .SetProperty(i => i.CompletionReason, reason)
                    .SetProperty(i => i.CompletionNotes, completionNotes)
                    .SetProperty(i => i.CompleteTreatmentId, completeTreatmentId),
                cancellationToken
            );

        return updated == 0 ? null : await InstanceByIdAsync(db, instanceId, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<List<TrackerInstanceEntity>> GetRunningStartedByAsync(
        IReadOnlyCollection<string> startTreatmentIds,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        if (startTreatmentIds.Count == 0)
            return [];

        var ids = startTreatmentIds.ToList();
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => i.CompletedAt == null && i.StartTreatmentId != null && ids.Contains(i.StartTreatmentId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<bool> HasRunEndedAfterAsync(
        Guid definitionId,
        DateTime at,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return await lease.Context.TrackerInstances.AnyAsync(
            i => i.DefinitionId == definitionId && i.CompletedAt > at,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public virtual async Task<TrackerStartRevert?> RevertStartAsync(
        Guid definitionId,
        string startTreatmentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var removed = await db.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(
            i => i.DefinitionId == definitionId && i.StartTreatmentId == startTreatmentId && i.CompletedAt == null,
            cancellationToken
        );
        if (removed == null)
            return null;

        var deleted = await db.TrackerInstances
            .Where(i => i.Id == removed.Id && i.CompletedAt == null)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            return null;

        var replaced = db.TrackerInstances
            .Where(i => i.DefinitionId == definitionId && i.CompleteTreatmentId == startTreatmentId);
        var reopenedIds = await replaced.Select(i => i.Id).ToListAsync(cancellationToken);
        await replaced.ExecuteUpdateAsync(
            s => s
                .SetProperty(i => i.CompletedAt, (DateTime?)null)
                .SetProperty(i => i.CompletionReason, (CompletionReason?)null)
                .SetProperty(i => i.CompletionNotes, (string?)null)
                .SetProperty(i => i.CompleteTreatmentId, (string?)null),
            cancellationToken
        );

        var reopened = await db.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => reopenedIds.Contains(i.Id))
            .ToListAsync(cancellationToken);
        return new TrackerStartRevert(removed, reopened);
    }

    private const int SuccessionLockClass = 0x4E54_524B;

    /// <inheritdoc />
    /// <remarks>
    /// Runs as a <see cref="DbUnitOfWork"/>: this repository's own when it was constructed over one,
    /// else a new unit, taking the definition's lock (<see cref="AdvisoryLockExtensions"/>) before
    /// the work and handing it a repository over the unit. Opening the connection sets the tenant
    /// GUCs (TenantConnectionInterceptor), so every statement in the transaction runs under the
    /// context's tenant.
    /// </remarks>
    public virtual async Task<T> ExecuteUnderDefinitionLockAsync<T>(
        Guid definitionId,
        Func<ITrackerRepository, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_unit is not null)
            return await CommitUnderLockAsync(_unit, this, definitionId, work, verifySucceeded, cancellationToken);

        await using var unit = await DbUnitOfWork.OpenAsync(_contexts!, cancellationToken);
        return await CommitUnderLockAsync(
            unit, new TrackerRepository(unit), definitionId, work, verifySucceeded, cancellationToken);
    }

    private static Task<T> CommitUnderLockAsync<T>(
        DbUnitOfWork unit,
        ITrackerRepository bound,
        Guid definitionId,
        Func<ITrackerRepository, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded,
        CancellationToken cancellationToken) =>
        unit.CommitAsync(
            async ct =>
            {
                await unit.Context.LockForTransactionAsync(SuccessionLockClass, definitionId, ct);
                return await work(bound, ct);
            },
            verifySucceeded is null ? null : (result, ct) => verifySucceeded(bound, result, ct),
            ct: cancellationToken
        );

    /// <summary>
    /// Acknowledge/snooze a tracker instance
    /// </summary>
    /// <param name="instanceId">The identifier of the instance to acknowledge.</param>
    /// <param name="snoozeMins">The number of minutes to snooze.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if recorded, otherwise false.</returns>
    public virtual async Task<bool> AckInstanceAsync(
        Guid instanceId,
        int snoozeMins,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var instance = await db.TrackerInstances.FirstOrDefaultAsync(
            i => i.Id == instanceId,
            cancellationToken
        );

        if (instance == null)
            return false;

        instance.LastAckedAt = DateTime.UtcNow;
        instance.AckSnoozeMins = snoozeMins;

        var result = await db.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete a tracker instance
    /// </summary>
    /// <param name="id">The unique identifier of the instance to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if deleted, otherwise false.</returns>
    public virtual async Task<bool> DeleteInstanceAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var instance = await db.TrackerInstances.FirstOrDefaultAsync(
            i => i.Id == id,
            cancellationToken
        );

        if (instance == null)
            return false;

        db.TrackerInstances.Remove(instance);
        var result = await db.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion

    #region Presets

    /// <summary>
    /// Get all presets for a user
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An array of tracker presets for the user.</returns>
    public virtual async Task<TrackerPresetEntity[]> GetPresetsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerPresets.AsNoTracking()
            .Include(p => p.Definition)
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Get a specific preset by ID
    /// </summary>
    /// <param name="id">The unique identifier of the preset.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracker preset, or null if not found.</returns>
    public virtual async Task<TrackerPresetEntity?> GetPresetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerPresets.AsNoTracking()
            .Include(p => p.Definition)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    /// <summary>
    /// Create a new preset
    /// </summary>
    /// <param name="preset">The preset entity to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created tracker preset.</returns>
    public virtual async Task<TrackerPresetEntity> CreatePresetAsync(
        TrackerPresetEntity preset,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        preset.Id = Guid.CreateVersion7();
        preset.CreatedAt = DateTime.UtcNow;

        db.TrackerPresets.Add(preset);
        await db.SaveChangesAsync(cancellationToken);

        return preset;
    }

    /// <summary>
    /// Delete a preset
    /// </summary>
    /// <param name="id">The unique identifier of the preset to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if deleted, otherwise false.</returns>
    public virtual async Task<bool> DeletePresetAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var preset = await db.TrackerPresets.FirstOrDefaultAsync(
            p => p.Id == id,
            cancellationToken
        );

        if (preset == null)
            return false;

        db.TrackerPresets.Remove(preset);
        var result = await db.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    #endregion
}
