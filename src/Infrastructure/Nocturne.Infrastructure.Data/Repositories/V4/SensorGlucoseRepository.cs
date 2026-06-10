using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing sensor glucose (CGM) records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class SensorGlucoseRepository : ISensorGlucoseRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<SensorGlucoseRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorGlucoseRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public SensorGlucoseRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<SensorGlucoseRepository> logger
    )
    {
        _contextFactory = contextFactory;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets sensor glucose records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="nativeOnly">Whether to return only native records.</param>
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of sensor glucose records.</returns>
    public async Task<IEnumerable<SensorGlucose>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        if (nativeOnly)
            query = query.Where(e => e.LegacyId == null);

        // Exclude non-primary duplicates from cross-connector deduplication
        query = query.Where(b => !ctx.LinkedRecords
            .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == b.Id));

        // Keyset cursor — when provided, replaces OFFSET with a WHERE clause
        // that seeks directly to the cursor position. O(limit) vs O(offset + limit).
        if (afterTimestamp.HasValue && afterId.HasValue)
        {
            query = descending
                ? query.Where(e => e.Timestamp < afterTimestamp.Value
                    || (e.Timestamp == afterTimestamp.Value && e.Id < afterId.Value))
                : query.Where(e => e.Timestamp > afterTimestamp.Value
                    || (e.Timestamp == afterTimestamp.Value && e.Id > afterId.Value));
        }

        query = descending
            ? query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id);

        if (!afterTimestamp.HasValue || !afterId.HasValue)
        {
            query = query.Skip(offset);
        }

        var entities = await query.Take(limit).ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a sensor glucose record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The sensor glucose record, or null if not found.</returns>
    public async Task<SensorGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.SensorGlucose.FindAsync([id], ct);
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a sensor glucose record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The sensor glucose record, or null if not found.</returns>
    public async Task<SensorGlucose?> GetByLegacyIdAsync(
        string legacyId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.SensorGlucose.FirstOrDefaultAsync(
            e => e.LegacyId == legacyId,
            ct
        );
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new sensor glucose record.
    /// </summary>
    /// <param name="model">The sensor glucose record to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created sensor glucose record.</returns>
    public async Task<SensorGlucose> CreateAsync(
        SensorGlucose model,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = SensorGlucoseMapper.ToEntity(model);
        ctx.SensorGlucose.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing sensor glucose record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated sensor glucose record.</returns>
    public async Task<SensorGlucose> UpdateAsync(
        Guid id,
        SensorGlucose model,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        SensorGlucoseMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a sensor glucose record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<SensorGlucose> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.SensorGlucose.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted SensorGlucose {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SensorGlucose>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.SensorGlucose.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SensorGlucose>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.SensorGlucose.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.SensorGlucose.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Counts sensor glucose records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets sensor glucose records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of matching records.</returns>
    public async Task<IEnumerable<SensorGlucose>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx
            .SensorGlucose.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a sensor glucose record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Performs a bulk creation of sensor glucose records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<SensorGlucose>> BulkCreateAsync(
        IEnumerable<SensorGlucose> records,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            var entities = records.Select(SensorGlucoseMapper.ToEntity).ToList();
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
            // in place — so timezone re-correction moves a reading's timestamp instead of duplicating
            // it. Everything else falls through to the LegacyId/insert path below. Mirrors BolusRepository.
            var updatedEntities = new List<SensorGlucoseEntity>();
            var syncKeyed = entities
                .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
                .ToList();

            if (syncKeyed.Count > 0)
            {
                var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
                var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

                var existingRows = await ctx.SensorGlucose.IgnoreQueryFilters()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
                    .ToListAsync(ct);

                var existingByKey = existingRows
                    .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
                    .ToDictionary(g => g.Key, g => g.First());

                var toInsert = new List<SensorGlucoseEntity>();
                foreach (var entity in entities)
                {
                    var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                        && !string.IsNullOrEmpty(entity.SyncIdentifier);
                    if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
                    {
                        var domain = SensorGlucoseMapper.ToDomainModel(entity);
                        SensorGlucoseMapper.UpdateEntity(existing, domain);
                        updatedEntities.Add(existing);
                    }
                    else
                    {
                        toInsert.Add(entity);
                    }
                }

                if (updatedEntities.Count > 0)
                    await ctx.SaveChangesAsync(ct);

                entities = toInsert;
            }

            // Batch-level dedup: keep first occurrence per LegacyId
            entities = entities
                .GroupBy(e => e.LegacyId ?? e.Id.ToString())
                .Select(g => g.First())
                .ToList();

            // DB-level dedup: filter out records whose LegacyId already exists
            var legacyIds = entities
                .Where(e => !string.IsNullOrEmpty(e.LegacyId))
                .Select(e => e.LegacyId!)
                .ToHashSet();

            if (legacyIds.Count > 0)
            {
                var blockedLegacyIds = await ctx.GetBlockingLegacyIdsAsync<SensorGlucoseEntity>(legacyIds, ct);

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !blockedLegacyIds.Contains(e.LegacyId))
                    .ToList();
            }

            if (entities.Count == 0 && updatedEntities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
            }

            const int batchSize = 500;
            foreach (var batch in entities.Chunk(batchSize))
            {
                ctx.SensorGlucose.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);

            // Insert-time deduplication: link saved records to canonical groups. Include the in-place
            // updates (re-corrected timestamps) so their canonical grouping is re-driven on the new mills.
            var saved = entities.Concat(updatedEntities).ToList();
            try
            {
                var dedupInputs = saved.Select(e => new DeduplicationInput(
                    RecordId: e.Id,
                    Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    DataSource: e.DataSource ?? "unknown",
                    Criteria: new MatchCriteria { GlucoseValue = e.Mgdl, GlucoseTolerance = 1.0 }
                )).ToList();

                await _deduplicationService.DeduplicateBatchAsync(RecordType.SensorGlucose, dedupInputs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "SensorGlucose", saved.Count);
            }

            return saved.Select(SensorGlucoseMapper.ToDomainModel);
        });
    }

    /// <summary>
    /// Gets the timestamp of the latest sensor glucose record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetLatestTimestampAsync(
        string? source = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Gets the timestamp of the oldest sensor glucose record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The oldest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetOldestTimestampAsync(
        string? source = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MinAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Counts sensor glucose records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of matching records.</returns>
    public async Task<int> CountBySourceAsync(string source, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.SensorGlucose
            .AsNoTracking()
            .Where(e => e.DataSource == source)
            .CountAsync(ct);
    }

    /// <summary>
    /// Deletes all sensor glucose records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.Where(e => e.DataSource == source), _auditContext, ct);
    }

    /// <summary>
    /// Deletes all sensor glucose records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Exclusive end, or null for no upper bound.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await ctx.AuditedSoftDeleteAsync(query, _auditContext, ct);
    }
}
