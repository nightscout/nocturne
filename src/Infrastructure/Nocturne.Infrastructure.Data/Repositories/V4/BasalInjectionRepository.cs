using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing <see cref="BasalInjection"/> records (discrete long-acting basal
/// insulin injections, MDI). Soft-deletes via <c>DeletedAt</c>; the global query filter
/// configured in <see cref="NocturneDbContext"/> excludes soft-deleted rows from reads.
/// </summary>
public class BasalInjectionRepository : IBasalInjectionRepository
{
    private readonly NocturneDbContext _context;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<BasalInjectionRepository> _logger;
    private readonly IV4RecordBroadcaster<BasalInjection>? _broadcaster;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasalInjectionRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    public BasalInjectionRepository(
        NocturneDbContext context,
        IAuditContext auditContext,
        ILogger<BasalInjectionRepository> logger,
        IV4RecordBroadcaster<BasalInjection>? broadcaster = null)
    {
        _context = context;
        _auditContext = auditContext;
        _logger = logger;
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Fires the native V4 broadcast for a just-committed write — but only for <see cref="WriteOrigin.Live"/>
    /// writes (backfill imports stay silent). Mirrors the gate in <c>V4RepositoryBase.RaiseBroadcastAsync</c>.
    /// </summary>
    private Task RaiseBroadcastAsync(
        IReadOnlyList<BasalInjection> created,
        IReadOnlyList<BasalInjection> updated,
        IReadOnlyList<Guid> deletedIds,
        WriteOrigin origin,
        CancellationToken ct)
        => V4RecordBroadcast.RaiseAsync(_broadcaster, created, updated, deletedIds, origin, ct);

    /// <summary>
    /// Gets basal injection records based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of basal injection records.</returns>
    public async Task<IEnumerable<BasalInjection>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        var query = _context.BasalInjections.AsNoTracking().AsQueryable();
        if (from is { } fromValue)
            query = query.Where(e => e.Timestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(e => e.Timestamp <= toValue);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a basal injection record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The basal injection record, or null if not found.</returns>
    public async Task<BasalInjection?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Use FirstOrDefaultAsync instead of FindAsync so the soft-delete global query
        // filter (WHERE DeletedAt IS NULL) is always applied. FindAsync checks the change
        // tracker first and can return a cached soft-deleted entity.
        var entity = await _context.BasalInjections
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        return entity is null ? null : BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new basal injection record. When <c>DataSource</c> and <c>SyncIdentifier</c>
    /// match an existing row for this tenant, the record is updated in place (upsert) rather
    /// than inserted — making the operation idempotent for connector replays.
    /// Tenant scoping is implicit via the DbContext's RLS-equivalent query filter.
    /// </summary>
    /// <remarks>
    /// The controller layer has its own idempotency check that returns the existing record
    /// unchanged (HTTP semantics). This repository-level upsert exists for non-HTTP callers
    /// (connectors, background services) that need "latest wins" semantics on replay.
    /// </remarks>
    /// <param name="model">The basal injection to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated basal injection record.</returns>
    public async Task<BasalInjection> CreateAsync(BasalInjection model, WriteOrigin origin, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await _context.BasalInjections
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                BasalInjectionMapper.UpdateEntity(existing, model);
                await _context.SaveChangesAsync(ct);
                var upserted = BasalInjectionMapper.ToDomainModel(existing);
                // An in-place upsert broadcasts as an update.
                await RaiseBroadcastAsync([], [upserted], [], origin, ct);
                return upserted;
            }
        }

        var entity = BasalInjectionMapper.ToEntity(model);
        _context.BasalInjections.Add(entity);
        await _context.SaveChangesAsync(ct);
        var created = BasalInjectionMapper.ToDomainModel(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
    }

    /// <summary>
    /// Updates an existing basal injection record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated basal injection record.</returns>
    public async Task<BasalInjection> UpdateAsync(Guid id, BasalInjection model, WriteOrigin origin, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalInjections.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new KeyNotFoundException($"BasalInjection {id} not found");
        BasalInjectionMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        var updated = BasalInjectionMapper.ToDomainModel(entity);
        await RaiseBroadcastAsync([], [updated], [], origin, ct);
        return updated;
    }

    /// <summary>
    /// Soft-deletes a basal injection record by setting <c>DeletedAt</c>. The row remains
    /// in the database but is excluded from reads by the global query filter.
    /// The <c>MutationAuditInterceptor</c> writes a "delete" audit entry automatically when
    /// it observes the null → non-null transition on <c>DeletedAt</c>.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalInjections.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new KeyNotFoundException($"BasalInjection {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        await RaiseBroadcastAsync([], [], [id], origin, ct);
    }

    /// <inheritdoc />
    public async Task<BasalInjection> RestoreAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        var entity = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted BasalInjection {id} not found");
        entity.DeletedAt = null;
        await _context.SaveChangesAsync(ct);
        // A restored record reappears in the dataset: broadcast it as a create so clients re-add it.
        var restored = BasalInjectionMapper.ToDomainModel(entity);
        await RaiseBroadcastAsync([restored], [], [], origin, ct);
        return restored;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BasalInjection>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        var entities = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await _context.SaveChangesAsync(ct);
        var restored = entities.Select(BasalInjectionMapper.ToDomainModel).ToList();
        await RaiseBroadcastAsync(restored, [], [], origin, ct);
        return restored;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BasalInjection>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        var entities = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        return await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Returns the timestamp of the most recently stored basal injection, optionally scoped to a data source.
    /// Used by connectors to resume per-source sync without re-fetching already-stored data.
    /// </summary>
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        var query = _context.BasalInjections.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Counts basal injection records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.BasalInjections.AsNoTracking().AsQueryable();
        if (from is { } fromValue)
            query = query.Where(e => e.Timestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(e => e.Timestamp <= toValue);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Bulk-create basal injection records. Atomic (the whole insert runs in one transaction via the
    /// connection's execution strategy) and audit-aware: rows whose latest delete was user-initiated
    /// stay blocked from resync re-creation. Deduplicates by (DataSource, SyncIdentifier) upsert and by
    /// LegacyId. Mirrors the established <see cref="BolusRepository"/> bulk path, minus
    /// DeduplicationService participation (BasalInjection is LegacyId-only / SyncId-keyed, never
    /// cross-connector dedup-linked).
    /// </summary>
    public async Task<IEnumerable<BasalInjection>> BulkCreateAsync(IEnumerable<BasalInjection> records, WriteOrigin origin, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            var entities = records.Select(BasalInjectionMapper.ToEntity).ToList();
            if (entities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
            }

            // Intra-batch SyncIdentifier dedup: keep last occurrence per (DataSource, SyncIdentifier).
            // Records without both keys keep a unique grouping key so they're not collapsed.
            entities = entities
                .GroupBy(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier)
                    ? $"sync|{e.DataSource}|{e.SyncIdentifier}"
                    : $"id|{e.Id}")
                .Select(g => g.Last())
                .ToList();

            // DB-level SyncIdentifier upsert: rows matched by (DataSource, SyncIdentifier) are updated
            // in place rather than inserted — so a connector replay updates the existing row instead of
            // hitting the partial unique index. Everything else falls through to the LegacyId/insert
            // path below. Mirrors BolusRepository.
            var updatedEntities = new List<BasalInjectionEntity>();
            var syncKeyed = entities
                .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
                .ToList();

            if (syncKeyed.Count > 0)
            {
                var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
                var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

                var existingRows = await _context.BasalInjections.IgnoreQueryFilters()
                    .Where(e => e.TenantId == _context.TenantId)
                    .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
                    .ToListAsync(ct);

                var existingByKey = existingRows
                    .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
                    .ToDictionary(g => g.Key, g => g.First());

                var toInsert = new List<BasalInjectionEntity>();
                foreach (var entity in entities)
                {
                    var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                        && !string.IsNullOrEmpty(entity.SyncIdentifier);
                    if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
                    {
                        // Update in place — mirror the single-record CreateAsync path via the mapper.
                        var domain = BasalInjectionMapper.ToDomainModel(entity);
                        BasalInjectionMapper.UpdateEntity(existing, domain);
                        updatedEntities.Add(existing);
                    }
                    else
                    {
                        toInsert.Add(entity);
                    }
                }

                if (updatedEntities.Count > 0)
                {
                    // Persist updates before the insert-chunking loop clears the tracker.
                    await _context.SaveChangesAsync(ct);
                }

                entities = toInsert;
            }

            // Batch-level dedup: keep first occurrence per LegacyId
            entities = entities
                .GroupBy(e => e.LegacyId ?? e.Id.ToString())
                .Select(g => g.First())
                .ToList();

            // DB-level dedup: filter out records whose LegacyId is blocking (active row, or a
            // soft-deleted row whose latest delete was user-initiated). System-sweep deletes stay
            // re-creatable. Replaces the inline existence check, which ignored the user-delete flag.
            var legacyIds = entities
                .Where(e => !string.IsNullOrEmpty(e.LegacyId))
                .Select(e => e.LegacyId!)
                .ToHashSet();

            if (legacyIds.Count > 0)
            {
                var blockedLegacyIds = await _context.GetBlockingLegacyIdsAsync<BasalInjectionEntity>(legacyIds, ct);

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !blockedLegacyIds.Contains(e.LegacyId))
                    .ToList();
            }

            if (entities.Count > 0)
            {
                const int batchSize = 500;
                foreach (var batch in entities.Chunk(batchSize))
                {
                    _context.BasalInjections.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                    _context.ChangeTracker.Clear();
                }
            }

            await tx.CommitAsync(ct);
            // Inserts broadcast as create; in-place upserts broadcast as update (unconditionally —
            // no material-change diffing on this off-base path).
            var insertedModels = entities.Select(BasalInjectionMapper.ToDomainModel).ToList();
            var updatedModels = updatedEntities.Select(BasalInjectionMapper.ToDomainModel).ToList();
            await RaiseBroadcastAsync(insertedModels, updatedModels, [], origin, ct);
            return updatedModels.Concat(insertedModels);
        });
    }

    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default)
    {
        var entities = await _context.BasalInjections
            .Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
            .ToListAsync(ct);

        if (entities.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var entity in entities)
            entity.DeletedAt = now;

        await _context.SaveChangesAsync(ct);
        await RaiseBroadcastAsync([], [], entities.Select(e => e.Id).ToList(), origin, ct);
        return entities.Count;
    }

    /// <summary>
    /// Finds a single basal injection by data source and sync identifier. The global query
    /// filter automatically scopes the lookup to the current tenant and excludes soft-deleted rows.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    public async Task<BasalInjection?> FindBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        var entity = await _context.BasalInjections
            .FirstOrDefaultAsync(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);
        return entity is null ? null : BasalInjectionMapper.ToDomainModel(entity);
    }
}
