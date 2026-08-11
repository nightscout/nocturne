using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing pump snapshot records (point-in-time pump state) in the database. Inherits
/// the shared CRUD, soft-delete and LegacyId-deduplicated bulk-insert surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the pump-specific queries and the
/// (DataSource, SyncIdentifier) upsert below.
/// </summary>
public class PumpSnapshotRepository : V4RepositoryBase<PumpSnapshot, PumpSnapshotEntity>, IPumpSnapshotRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PumpSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    // logger is unused but retained for DI + direct test construction.
    public PumpSnapshotRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<PumpSnapshotRepository> logger,
        IV4RecordBroadcaster<PumpSnapshot>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override PumpSnapshotEntity ToEntity(PumpSnapshot model) => PumpSnapshotMapper.ToEntity(model);

    /// <inheritdoc />
    protected override PumpSnapshot ToDomain(PumpSnapshotEntity entity) => PumpSnapshotMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(PumpSnapshotEntity target, PumpSnapshot source) =>
        PumpSnapshotMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<PumpSnapshot?> GetLatestBeforeAsync(DateTime timestamp, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots
            .AsNoTracking()
            .Where(e => e.Timestamp < timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<PumpSnapshot?> GetLatestAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.PumpSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets pump snapshots by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching pump snapshots.</returns>
    public async Task<IEnumerable<PumpSnapshot>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.PumpSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public Task<IEnumerable<PumpSnapshot>> BulkUpsertAsync(
        IEnumerable<PumpSnapshot> records,
        WriteOrigin origin, CancellationToken ct = default)
        => BulkWriteAsync(records, SplitBySyncKeyAsync, origin, ct);

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place. Soft-deleted rows are excluded: the partial
    /// unique index ignores them, so a re-upload after a delete inserts a fresh row instead of writing
    /// into the deleted one.
    /// </summary>
    private static async Task<UpsertSplit> SplitBySyncKeyAsync(
        NocturneDbContext ctx, List<PumpSnapshotEntity> entities, CancellationToken ct)
    {
        // Records without both keys keep a unique grouping key so they're not collapsed.
        entities = entities
            .GroupBy(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier)
                ? $"sync|{e.DataSource}|{e.SyncIdentifier}"
                : $"id|{e.Id}")
            .Select(g => g.Last())
            .ToList();

        var syncKeyed = entities
            .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
            .ToList();

        var updatedEntities = new List<PumpSnapshotEntity>();
        var materiallyChanged = new List<PumpSnapshotEntity>();
        if (syncKeyed.Count == 0)
            return new UpsertSplit(updatedEntities, materiallyChanged, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        var existingRows = await ctx.PumpSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt == null)
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.First());

        var toInsert = new List<PumpSnapshotEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                PumpSnapshotMapper.UpdateEntity(existing, PumpSnapshotMapper.ToDomainModel(entity));
                updatedEntities.Add(existing);
                // Capture material changes now, before SaveChanges clears the modified flags.
                if (V4MaterialChange.HasMaterialChange(ctx.Entry(existing)))
                    materiallyChanged.Add(existing);
            }
            else
            {
                toInsert.Add(entity);
            }
        }

        if (updatedEntities.Count > 0)
        {
            // Persist updates before the insert-chunking loop clears the tracker.
            await ctx.SaveChangesAsync(ct);
        }

        return new UpsertSplit(updatedEntities, materiallyChanged, toInsert);
    }
}
