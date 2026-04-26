using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing APS snapshots in the database.
/// </summary>
public class ApsSnapshotRepository : IApsSnapshotRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<ApsSnapshotRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApsSnapshotRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public ApsSnapshotRepository(NocturneDbContext context, ILogger<ApsSnapshotRepository> logger)
    {
        _context = context;
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
        var query = _context.ApsSnapshots.AsNoTracking().AsQueryable();
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
        var entity = await _context.ApsSnapshots.FindAsync([id], ct);
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
        var entity = await _context.ApsSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new APS snapshot record.
    /// </summary>
    /// <param name="model">The APS snapshot to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created APS snapshot.</returns>
    public async Task<ApsSnapshot> CreateAsync(ApsSnapshot model, CancellationToken ct = default)
    {
        var entity = ApsSnapshotMapper.ToEntity(model);
        _context.ApsSnapshots.Add(entity);
        await _context.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing APS snapshot record.
    /// </summary>
    /// <param name="id">The unique identifier of the snapshot to update.</param>
    /// <param name="model">The updated snapshot data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated APS snapshot.</returns>
    public async Task<ApsSnapshot> UpdateAsync(Guid id, ApsSnapshot model, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        ApsSnapshotMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes an APS snapshot record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        _context.ApsSnapshots.Remove(entity);
        await _context.SaveChangesAsync(ct);
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

        var entities = await _context.ApsSnapshots
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
        var since = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;
        var entities = await _context.ApsSnapshots
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
        var query = _context.ApsSnapshots.AsNoTracking().AsQueryable();
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
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.ApsSnapshots
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }
}
