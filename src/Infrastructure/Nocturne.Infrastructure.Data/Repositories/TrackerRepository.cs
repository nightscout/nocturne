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
    private readonly TimeProvider _time;

    /// <summary>A repository whose calls each open their own context.</summary>
    public TrackerRepository(INocturneDbContextFactory contexts, TimeProvider? timeProvider = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A repository whose calls run on <paramref name="unit"/>, which the caller owns.</summary>
    public TrackerRepository(DbUnitOfWork unit, TimeProvider? timeProvider = null)
    {
        _unit = unit;
        _time = timeProvider ?? TimeProvider.System;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    #region Definitions

    /// <inheritdoc />
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

    /// <inheritdoc />
    public virtual async Task<List<TrackerDefinitionEntity>> GetDefinitionsByCategoryAsync(
        TrackerCategory category,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .Where(d => d.Category == category)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
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
        definition.CreatedAt = _time.GetUtcNow().UtcDateTime;

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
        existing.EndEventTypes = updated.EndEventTypes;
        existing.Covers = updated.Covers;
        existing.DeviceComponent = updated.DeviceComponent;
        existing.TriggerNotesContains = updated.TriggerNotesContains;
        existing.LifespanHours = updated.LifespanHours;
        existing.IsFavorite = updated.IsFavorite;
        existing.DashboardVisibility = updated.DashboardVisibility;
        existing.StartEventType = updated.StartEventType;
        existing.CompletionEventType = updated.CompletionEventType;
        existing.LowReservoirUnits = updated.LowReservoirUnits;
        existing.LowReservoirUrgency = updated.LowReservoirUrgency;
        existing.Mode = updated.Mode;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;

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

    /// <inheritdoc />
    public virtual async Task<TrackerInstanceEntity[]> GetActiveInstancesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => i.CompletedAt == null)
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
    public virtual async Task<Dictionary<Guid, ActiveTrackerReference>> GetActiveTrackerReferencesAsync(
        IReadOnlySet<Guid> definitionIds,
        DateTime at,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        if (definitionIds.Count == 0)
            return new Dictionary<Guid, ActiveTrackerReference>();

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
                i.RecordedAt,
                i.Definition!.Mode,
            })
            .ToListAsync(cancellationToken);

        var references = new Dictionary<Guid, ActiveTrackerReference>();
        foreach (var instance in instances)
        {
            // Newest-first ordering: the first instance seen per definition wins.
            if (references.ContainsKey(instance.DefinitionId))
                continue;

            var reference = instance.Mode == TrackerMode.Event
                ? instance.ScheduledAt
                : instance.StartedAt;
            if (reference is { } referenceAt)
                references[instance.DefinitionId] = new ActiveTrackerReference(
                    referenceAt, instance.RecordedAt, IsScheduled: instance.Mode == TrackerMode.Event);
        }

        return references;
    }

    /// <inheritdoc />
    public virtual async Task<TrackerInstanceEntity[]> GetCompletedInstancesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => i.CompletedAt != null)
            .OrderByDescending(i => i.CompletedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerInstanceEntity[]> GetUpcomingInstancesAsync(
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
            .Where(i => i.CompletedAt == null && i.Definition.LifespanHours != null)
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
    /// <param name="createdBy">The subject starting the instance.</param>
    /// <param name="startNotes">Optional initial notes.</param>
    /// <param name="startTreatmentId">Optional starting treatment ID.</param>
    /// <param name="startedAt">Optional start timestamp.</param>
    /// <param name="scheduledAt">Optional scheduled timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The newly started tracker instance.</returns>
    public virtual async Task<TrackerInstanceEntity> StartInstanceAsync(
        Guid definitionId,
        string createdBy,
        string? startNotes = null,
        string? startTreatmentId = null,
        DateTime? startedAt = null,
        DateTime? scheduledAt = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var now = _time.GetUtcNow().UtcDateTime;
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            CreatedBy = createdBy,
            DefinitionId = definitionId,
            StartedAt = startedAt ?? now,
            RecordedAt = now,
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
        var at = completedAt ?? _time.GetUtcNow().UtcDateTime;

        // Lifted so a clamped member can complete a run at a time before the window. It targets only
        // running instances, which the clamp shows (see ShareDataCategories.HiddenSpanEndColumns), and
        // reads back only the one it completed.
        return await db.WriteWithHistoryClampLiftedAsync(
            async token =>
            {
                var updated = await db
                    .TrackerInstances.Where(i => i.Id == instanceId && i.CompletedAt == null)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(i => i.CompletedAt, at)
                            .SetProperty(i => i.CompletionReason, reason)
                            .SetProperty(i => i.CompletionNotes, completionNotes)
                            .SetProperty(i => i.CompleteTreatmentId, completeTreatmentId),
                        token
                    );

                return updated == 0 ? null : await InstanceByIdAsync(db, instanceId, token);
            },
            cancellationToken);
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

    /// <inheritdoc />
    public virtual async Task<List<Guid>> GetDefinitionsCompletedByAsync(
        IReadOnlyCollection<string> completeTreatmentIds,
        CancellationToken cancellationToken = default
    )
    {
        if (completeTreatmentIds.Count == 0)
            return [];

        await using var lease = await OpenAsync(cancellationToken);
        var ids = completeTreatmentIds.ToList();
        return await lease.Context.TrackerInstances
            .Where(i => i.CompleteTreatmentId != null && ids.Contains(i.CompleteTreatmentId))
            .Select(i => i.DefinitionId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<List<TrackerInstanceEntity>> ReopenCompletedByAsync(
        Guid definitionId,
        IReadOnlyCollection<string> completeTreatmentIds,
        CancellationToken cancellationToken = default
    )
    {
        if (completeTreatmentIds.Count == 0)
            return [];

        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var ids = completeTreatmentIds.ToList();
        var ended = db.TrackerInstances
            .Where(i => i.DefinitionId == definitionId
                && i.CompleteTreatmentId != null && ids.Contains(i.CompleteTreatmentId))
            .Where(i => !db.TrackerInstances.Any(
                later => later.DefinitionId == definitionId && later.Id != i.Id && later.StartedAt > i.StartedAt));
        var reopenedIds = await ended.Select(i => i.Id).ToListAsync(cancellationToken);
        if (reopenedIds.Count == 0)
            return [];

        await db.TrackerInstances.Where(i => reopenedIds.Contains(i.Id)).ExecuteUpdateAsync(
            s => s
                .SetProperty(i => i.CompletedAt, (DateTime?)null)
                .SetProperty(i => i.CompletionReason, (CompletionReason?)null)
                .SetProperty(i => i.CompletionNotes, (string?)null)
                .SetProperty(i => i.CompleteTreatmentId, (string?)null),
            cancellationToken
        );

        return await db.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds)
            .Where(i => reopenedIds.Contains(i.Id))
            .ToListAsync(cancellationToken);
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
            unit, new TrackerRepository(unit, _time), definitionId, work, verifySucceeded, cancellationToken);
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

    private const int ConsumableLockClass = 0x4E54_434E;

    /// <inheritdoc />
    /// <remarks>
    /// The lock is keyed on the context's tenant, taken as <see cref="ExecuteUnderDefinitionLockAsync{T}"/>
    /// takes the definition's.
    /// </remarks>
    public virtual async Task<T> ExecuteUnderConsumableLockAsync<T>(
        Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_unit is not null)
            return await CommitUnderConsumableLockAsync(_unit, this, work, verifySucceeded, cancellationToken);

        await using var unit = await DbUnitOfWork.OpenAsync(_contexts!, cancellationToken);
        return await CommitUnderConsumableLockAsync(
            unit, new TrackerRepository(unit, _time), work, verifySucceeded, cancellationToken);
    }

    private static Task<T> CommitUnderConsumableLockAsync<T>(
        DbUnitOfWork unit,
        ITrackerRepository bound,
        Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded,
        CancellationToken cancellationToken) =>
        unit.CommitAsync(
            async ct =>
            {
                await unit.Context.LockForTransactionAsync(ConsumableLockClass, unit.Context.TenantId, ct);
                return await work(bound, unit, ct);
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

        instance.LastAckedAt = _time.GetUtcNow().UtcDateTime;
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

    /// <inheritdoc />
    public virtual async Task<TrackerPresetEntity[]> GetPresetsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        return await db
            .TrackerPresets.AsNoTracking()
            .Include(p => p.Definition)
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
        preset.CreatedAt = _time.GetUtcNow().UtcDateTime;

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
