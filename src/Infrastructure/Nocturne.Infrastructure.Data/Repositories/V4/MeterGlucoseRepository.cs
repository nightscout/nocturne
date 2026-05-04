using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing meter glucose (fingerstick) records in the database.
/// </summary>
public class MeterGlucoseRepository : IMeterGlucoseRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<MeterGlucoseRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeterGlucoseRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public MeterGlucoseRepository(NocturneDbContext context, ILogger<MeterGlucoseRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets meter glucose records based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of meter glucose records.</returns>
    public async Task<IEnumerable<MeterGlucose>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(MeterGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a meter glucose record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The meter glucose record, or null if not found.</returns>
    public async Task<MeterGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct);
        return entity is null ? null : MeterGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a meter glucose record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The meter glucose record, or null if not found.</returns>
    public async Task<MeterGlucose?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : MeterGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new meter glucose record.
    /// </summary>
    /// <param name="model">The meter glucose to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created meter glucose record.</returns>
    public async Task<MeterGlucose> CreateAsync(MeterGlucose model, CancellationToken ct = default)
    {
        var entity = MeterGlucoseMapper.ToEntity(model);
        _context.MeterGlucose.Add(entity);
        await _context.SaveChangesAsync(ct);
        return MeterGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing meter glucose record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated meter glucose record.</returns>
    public async Task<MeterGlucose> UpdateAsync(Guid id, MeterGlucose model, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"MeterGlucose {id} not found");
        MeterGlucoseMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return MeterGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a meter glucose record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"MeterGlucose {id} not found");
        _context.MeterGlucose.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts meter glucose records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets meter glucose records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of meter glucose records.</returns>
    public async Task<IEnumerable<MeterGlucose>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.MeterGlucose
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(MeterGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a meter glucose record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.MeterGlucose
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Gets the timestamp of the latest meter glucose record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Gets the timestamp of the oldest meter glucose record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The oldest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MinAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Deletes all meter glucose records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        return await _context.MeterGlucose
            .Where(e => e.DataSource == source)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes all meter glucose records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Exclusive end, or null for no upper bound.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await query.ExecuteDeleteAsync(ct);
    }
}
