using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Tracker operations (definitions, instances, presets). Each call runs
/// on its own context, or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
public class TrackerRepository : ITrackerRepository, ITrackerConsumableLock
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

    private static IQueryable<TrackerInstanceEntity> Runs(NocturneDbContext db) =>
        db.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition).ThenInclude(d => d.NotificationThresholds);

    private static IReadOnlyList<TrackerInstance> ToDomain(IEnumerable<TrackerInstanceEntity> rows) =>
        [.. rows.Select(TrackerMapper.ToDomain)];

    #region Definitions

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerDefinition>> GetAllDefinitionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var rows = await lease.Context
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(TrackerMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerDefinition>> GetDefinitionsByCategoryAsync(
        TrackerCategory category,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var rows = await lease.Context
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .Where(d => d.Category == category)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(TrackerMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<TrackerDefinition?> GetDefinitionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var row = await lease.Context
            .TrackerDefinitions.AsNoTracking()
            .Include(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        return row is null ? null : TrackerMapper.ToDomain(row);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerDefinition> CreateDefinitionAsync(
        TrackerDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var row = TrackerMapper.ToEntity(definition);
        row.Id = Guid.CreateVersion7();
        row.CreatedAt = _time.GetUtcNow().UtcDateTime;

        db.TrackerDefinitions.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        return TrackerMapper.ToDomain(row);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerDefinition?> UpdateDefinitionAsync(
        TrackerDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var existing = await db.TrackerDefinitions.FirstOrDefaultAsync(
            d => d.Id == definition.Id,
            cancellationToken
        );

        if (existing == null)
            return null;

        TrackerMapper.Apply(definition, existing);
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);

        await db.Entry(existing).Collection(d => d.NotificationThresholds).LoadAsync(cancellationToken);

        return TrackerMapper.ToDomain(existing);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public virtual async Task UpdateNotificationThresholdsAsync(
        Guid definitionId,
        IReadOnlyList<TrackerNotificationThreshold> thresholds,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var existing = await db
            .TrackerNotificationThresholds.Where(t => t.TrackerDefinitionId == definitionId)
            .ToListAsync(cancellationToken);

        db.TrackerNotificationThresholds.RemoveRange(existing);

        foreach (var threshold in thresholds)
        {
            var row = TrackerMapper.ToEntity(threshold);
            row.Id = Guid.CreateVersion7();
            row.TrackerDefinitionId = definitionId;
            db.TrackerNotificationThresholds.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task LinkThresholdRuleAsync(
        Guid thresholdId,
        Guid alertRuleId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        if (await db.TrackerNotificationThresholds.FindAsync([thresholdId], cancellationToken) is not { } row)
            return;

        row.AlertRuleId = alertRuleId;
        if (_unit is null)
            await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<Guid>> GetDefinitionsWithUnlinkedThresholdsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return await lease.Context.TrackerNotificationThresholds
            .AsNoTracking()
            .Where(t => t.AlertRuleId == null)
            .Select(t => t.TrackerDefinitionId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    #endregion

    #region Instances

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return ToDomain(await Runs(lease.Context)
            .Where(i => i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(cancellationToken));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetActiveInstancesForDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return ToDomain(await Runs(lease.Context)
            .Where(i => i.DefinitionId == definitionId && i.CompletedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(cancellationToken));
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
            .Where(i => ids.Contains(i.DefinitionId))
            .ActiveAt(i => i.StartedAt, i => i.CompletedAt, at)
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
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetCompletedInstancesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return ToDomain(await Runs(lease.Context)
            .Where(i => i.CompletedAt != null)
            .OrderByDescending(i => i.CompletedAt)
            .Take(limit)
            .ToListAsync(cancellationToken));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetUpcomingInstancesAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var instances = await Runs(lease.Context)
            .Where(i => i.CompletedAt == null && i.Definition.LifespanHours != null)
            .ToListAsync(cancellationToken);

        return ToDomain(instances.Where(i =>
        {
            var expectedEnd = i.StartedAt.AddHours(i.Definition.LifespanHours!.Value);
            return expectedEnd >= from && expectedEnd <= to;
        }));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetRunsOverlappingAsync(
        DateTime from,
        DateTime to,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return ToDomain(await lease.Context.TrackerInstances.AsNoTracking()
            .Include(i => i.Definition)
            .Where(i => i.StartedAt < to && (i.CompletedAt == null || i.CompletedAt > from))
            .OrderBy(i => i.StartedAt).Take(limit)
            .ToListAsync(cancellationToken));
    }

    /// <inheritdoc />
    public virtual async Task<bool> AnyRunInAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return await lease.Context.TrackerInstances.AnyAsync(
            e => e.StartedAt < to && (e.CompletedAt == null || e.CompletedAt > from), cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerInstance?> GetInstanceByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        return await InstanceByIdAsync(lease.Context, id, cancellationToken);
    }

    private static async Task<TrackerInstance?> InstanceByIdAsync(
        NocturneDbContext db, Guid id, CancellationToken cancellationToken) =>
        await Runs(db).FirstOrDefaultAsync(i => i.Id == id, cancellationToken) is { } row
            ? TrackerMapper.ToDomain(row)
            : null;

    /// <inheritdoc />
    public virtual async Task<TrackerInstance> StartInstanceAsync(
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

        return TrackerMapper.ToDomain(instance);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerInstance?> CompleteInstanceAsync(
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
    public virtual async Task<IReadOnlyList<TrackerInstance>> GetRunningStartedByAsync(
        IReadOnlyCollection<string> startTreatmentIds,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        if (startTreatmentIds.Count == 0)
            return [];

        var ids = startTreatmentIds.ToList();
        return ToDomain(await Runs(lease.Context)
            .Where(i => i.CompletedAt == null && i.StartTreatmentId != null && ids.Contains(i.StartTreatmentId))
            .ToListAsync(cancellationToken));
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
        var removed = await Runs(db).FirstOrDefaultAsync(
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

        var reopened = await Runs(db)
            .Where(i => reopenedIds.Contains(i.Id))
            .ToListAsync(cancellationToken);
        return new TrackerStartRevert(TrackerMapper.ToDomain(removed), ToDomain(reopened));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<Guid>> GetDefinitionsCompletedByAsync(
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
    public virtual async Task<IReadOnlyList<TrackerInstance>> ReopenCompletedByAsync(
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

        return ToDomain(await Runs(db)
            .Where(i => reopenedIds.Contains(i.Id))
            .ToListAsync(cancellationToken));
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
    public virtual async Task<T> ExecuteAsync<T>(
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public virtual async Task PurgeInstancesAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        await lease.Context.TrackerInstances.PurgeAsync(i => i.DefinitionId == definitionId, cancellationToken);
    }

    #endregion

    #region Presets

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TrackerPreset>> GetPresetsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var rows = await lease.Context
            .TrackerPresets.AsNoTracking()
            .Include(p => p.Definition)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(TrackerMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<TrackerPreset?> GetPresetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var row = await lease.Context
            .TrackerPresets.AsNoTracking()
            .Include(p => p.Definition)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return row is null ? null : TrackerMapper.ToDomain(row);
    }

    /// <inheritdoc />
    public virtual async Task<TrackerPreset> CreatePresetAsync(
        TrackerPreset preset,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await OpenAsync(cancellationToken);
        var db = lease.Context;
        var row = new TrackerPresetEntity
        {
            Id = Guid.CreateVersion7(),
            CreatedBy = preset.CreatedBy,
            Name = preset.Name,
            DefinitionId = preset.DefinitionId,
            DefaultStartNotes = preset.DefaultStartNotes,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };

        db.TrackerPresets.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        return (await GetPresetByIdAsync(row.Id, cancellationToken))!;
    }

    /// <inheritdoc />
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
