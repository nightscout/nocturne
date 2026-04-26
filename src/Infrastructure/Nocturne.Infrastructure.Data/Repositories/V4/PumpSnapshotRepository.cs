using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing pump snapshot records (point-in-time pump state) in the database.
/// </summary>
public class PumpSnapshotRepository : IPumpSnapshotRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<PumpSnapshotRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PumpSnapshotRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public PumpSnapshotRepository(NocturneDbContext context, ILogger<PumpSnapshotRepository> logger)
    {
        _context = context;
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
        var query = _context.PumpSnapshots.AsNoTracking().AsQueryable();
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
        var entity = await _context.PumpSnapshots.FindAsync([id], ct);
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
        var entity = await _context.PumpSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
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
        var entity = PumpSnapshotMapper.ToEntity(model);
        _context.PumpSnapshots.Add(entity);
        await _context.SaveChangesAsync(ct);
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
        var entity = await _context.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        PumpSnapshotMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a pump snapshot record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        _context.PumpSnapshots.Remove(entity);
        await _context.SaveChangesAsync(ct);
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

        var entities = await _context.PumpSnapshots
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
        var query = _context.PumpSnapshots.AsNoTracking().AsQueryable();
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
        return await _context.PumpSnapshots
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }
}
