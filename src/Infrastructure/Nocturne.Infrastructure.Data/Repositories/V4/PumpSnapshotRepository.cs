using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing pump snapshot records (point-in-time pump state) in the database.
/// </summary>
public class PumpSnapshotRepository : IPumpSnapshotRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<PumpSnapshotRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PumpSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public PumpSnapshotRepository(ITenantDbContextFactory contextFactory, ILogger<PumpSnapshotRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets pump snapshot records based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of pump snapshots.</returns>
    public async Task<IEnumerable<PumpSnapshot>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.PumpSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a pump snapshot record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The pump snapshot, or null if not found.</returns>
    public async Task<PumpSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots.FindAsync([id], ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a pump snapshot record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The pump snapshot, or null if not found.</returns>
    public async Task<PumpSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<PumpSnapshot?> GetLatestBeforeAsync(DateTime timestamp, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.PumpSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new pump snapshot record.
    /// </summary>
    /// <param name="model">The pump snapshot to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created pump snapshot.</returns>
    public async Task<PumpSnapshot> CreateAsync(PumpSnapshot model, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = PumpSnapshotMapper.ToEntity(model);
        ctx.PumpSnapshots.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing pump snapshot record.
    /// </summary>
    /// <param name="id">The unique identifier of the snapshot to update.</param>
    /// <param name="model">The updated snapshot data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated pump snapshot.</returns>
    public async Task<PumpSnapshot> UpdateAsync(Guid id, PumpSnapshot model, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        PumpSnapshotMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a pump snapshot record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PumpSnapshot> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted PumpSnapshot {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PumpSnapshot>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.PumpSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PumpSnapshot>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PumpSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.PumpSnapshots.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
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

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PumpSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Counts pump snapshot records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.PumpSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Deletes a pump snapshot record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.PumpSnapshots
            .Where(e => e.LegacyId == legacyId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PumpSnapshot>> BulkCreateAsync(
        IEnumerable<PumpSnapshot> records,
        CancellationToken ct = default)
    {
        var entities = records.Select(PumpSnapshotMapper.ToEntity).ToList();
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
                var existingRecords = await ctx.PumpSnapshots.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => legacyIds.Contains(e.LegacyId!))
                    .Select(e => new { e.LegacyId, IsSoftDeleted = e.DeletedAt != null })
                    .ToListAsync(ct);

                var existingSet = existingRecords.Select(r => r.LegacyId).ToHashSet();
                var softDeletedCount = existingRecords.Count(r => r.IsSoftDeleted);

                if (softDeletedCount > 0)
                    _logger.LogInformation(
                        "Skipped {Count} previously-deleted {Type} records during import",
                        softDeletedCount, "PumpSnapshot");

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
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
                ctx.PumpSnapshots.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            return entities.Select(PumpSnapshotMapper.ToDomainModel);
        });
    }
}
