using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing APS snapshots in the database.
/// </summary>
public class ApsSnapshotRepository : IApsSnapshotRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<ApsSnapshotRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApsSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public ApsSnapshotRepository(ITenantDbContextFactory contextFactory, ILogger<ApsSnapshotRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets APS snapshots based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of APS snapshots.</returns>
    public async Task<IEnumerable<ApsSnapshot>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets an APS snapshot by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The APS snapshot, or null if not found.</returns>
    public async Task<ApsSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.ApsSnapshots.FindAsync([id], ct);
        return entity is null ? null : ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets an APS snapshot by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The APS snapshot, or null if not found.</returns>
    public async Task<ApsSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.ApsSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new APS snapshot record.
    /// </summary>
    /// <param name="model">The APS snapshot to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created APS snapshot.</returns>
    public async Task<ApsSnapshot> CreateAsync(ApsSnapshot model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = ApsSnapshotMapper.ToEntity(model);
        ctx.ApsSnapshots.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing APS snapshot record.
    /// </summary>
    /// <param name="id">The unique identifier of the snapshot to update.</param>
    /// <param name="model">The updated snapshot data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated APS snapshot.</returns>
    public async Task<ApsSnapshot> UpdateAsync(Guid id, ApsSnapshot model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        ApsSnapshotMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes an APS snapshot record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ApsSnapshot> RestoreAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.ApsSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted ApsSnapshot {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ApsSnapshot>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.ApsSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ApsSnapshot>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.ApsSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.ApsSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
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

        await using var ctx = await _contextFactory.CreateAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;
        var entities = await ctx.ApsSnapshots
            .AsNoTracking()
            .Where(e => e.SysUpdatedAt >= since)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Counts APS snapshots within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Deletes an APS snapshot record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.ApsSnapshots
            .Where(e => e.LegacyId == legacyId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsOfAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking();
        if (source != null) query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestEnactedTimestampAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().Where(e => e.SensitivityRatio != null);
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var value = await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.SensitivityRatio)
            .FirstOrDefaultAsync(ct);
        return value is double v && double.IsFinite(v) ? (decimal)v : null;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ApsSnapshot>> BulkCreateAsync(
        IEnumerable<ApsSnapshot> records,
        WriteOrigin origin, CancellationToken ct = default)
    {
        var entities = records.Select(ApsSnapshotMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

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

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            if (legacyIds.Count > 0)
            {
                var blockedLegacyIds = await ctx.GetBlockingLegacyIdsAsync<ApsSnapshotEntity>(legacyIds, ct);

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !blockedLegacyIds.Contains(e.LegacyId))
                    .ToList();
            }

            if (entities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
            }

            const int batchSize = 500;
            foreach (var batch in entities.Chunk(batchSize))
            {
                ctx.ApsSnapshots.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            return entities.Select(ApsSnapshotMapper.ToDomainModel);
        });
    }
}
