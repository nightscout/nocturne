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
/// Repository for managing APS snapshots in the database. Inherits the shared CRUD, soft-delete and
/// LegacyId-deduplicated bulk-insert surface from <see cref="V4RepositoryBase{TModel,TEntity}"/> and
/// keeps only the APS-specific queries and the (DataSource, SyncIdentifier) upsert below.
/// </summary>
public class ApsSnapshotRepository : V4RepositoryBase<ApsSnapshot, ApsSnapshotEntity>, IApsSnapshotRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApsSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    // logger is unused but retained for DI + direct test construction.
    public ApsSnapshotRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<ApsSnapshotRepository> logger,
        IV4RecordBroadcaster<ApsSnapshot>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override ApsSnapshotEntity ToEntity(ApsSnapshot model) => ApsSnapshotMapper.ToEntity(model);

    /// <inheritdoc />
    protected override ApsSnapshot ToDomain(ApsSnapshotEntity entity) => ApsSnapshotMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(ApsSnapshotEntity target, ApsSnapshot source) =>
        ApsSnapshotMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApsIobCobPoint>> GetIobCobPointsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.ApsSnapshots.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .Select(e => new ApsIobCobPoint(e.Timestamp, e.Iob, e.Cob))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets APS snapshots by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching APS snapshots.</returns>
    public async Task<IEnumerable<ApsSnapshot>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ApsSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets APS snapshots modified since the given timestamp, ordered oldest-first.
    /// </summary>
    /// <param name="lastModifiedMills">Unix millisecond timestamp threshold.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching APS snapshots ordered by modification time ascending.</returns>
    public async Task<IEnumerable<ApsSnapshot>> GetModifiedSinceAsync(
        long lastModifiedMills, int limit = 1000, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;
        // Filter and order on the event Timestamp: it is the clock the V3 devicestatus DTO
        // reports as srvModified and the AAPS history cursor advances on, and it is the
        // indexed column. Filtering on the write clock (SysUpdatedAt) instead sets the cursor
        // below the returned rows' write time, so every poll re-matches them (an incremental-
        // sync loop). Strictly-greater (not >=) so the cursor record AAPS already holds is not
        // re-returned; the boundary record's sub-millisecond remainder is deduplicated by AAPS
        // rather than dropped (a >= cursor+1ms bound would silently skip sub-ms page splits).
        var entities = await ctx.ApsSnapshots
            .AsNoTracking()
            .Where(e => e.Timestamp > since)
            .OrderBy(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsOfAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestEnactedTimestampAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().Where(e => e.Enacted);
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Non-finite (Infinity/NaN) values from corrupt connector payloads are coerced to null rather than throwing.
    /// </remarks>
    public async Task<decimal?> GetLatestSensitivityRatioAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().Where(e => e.SensitivityRatio != null);
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var value = await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.SensitivityRatio)
            .FirstOrDefaultAsync(ct);
        return value is double v && double.IsFinite(v) ? (decimal)v : null;
    }

    /// <inheritdoc />
    public Task<IEnumerable<ApsSnapshot>> BulkUpsertAsync(
        IEnumerable<ApsSnapshot> records,
        WriteOrigin origin, CancellationToken ct = default)
        => BulkWriteAsync(records, SplitBySyncKeyAsync, origin, ct);

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place. Soft-deleted rows are excluded: the partial
    /// unique index ignores them, so a re-upload after a delete inserts a fresh row instead of writing
    /// into the deleted one.
    /// </summary>
    private static async Task<UpsertSplit> SplitBySyncKeyAsync(
        NocturneDbContext ctx, List<ApsSnapshotEntity> entities, CancellationToken ct)
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

        var updatedEntities = new List<ApsSnapshotEntity>();
        var materiallyChanged = new List<ApsSnapshotEntity>();
        if (syncKeyed.Count == 0)
            return new UpsertSplit(updatedEntities, materiallyChanged, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        var existingRows = await ctx.ApsSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt == null)
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.First());

        var toInsert = new List<ApsSnapshotEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                ApsSnapshotMapper.UpdateEntity(existing, ApsSnapshotMapper.ToDomainModel(entity));
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
