using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing calibration records in the database.
/// </summary>
public class CalibrationRepository : ICalibrationRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<CalibrationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public CalibrationRepository(NocturneDbContext context, ILogger<CalibrationRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets calibration records based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of calibrations.</returns>
    public async Task<IEnumerable<Calibration>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a calibration record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The calibration record, or null if not found.</returns>
    public async Task<Calibration?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct);
        return entity is null ? null : CalibrationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a calibration record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The calibration record, or null if not found.</returns>
    public async Task<Calibration?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : CalibrationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new calibration record.
    /// </summary>
    /// <param name="model">The calibration to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created calibration record.</returns>
    public async Task<Calibration> CreateAsync(Calibration model, CancellationToken ct = default)
    {
        var entity = CalibrationMapper.ToEntity(model);
        _context.Calibrations.Add(entity);
        await _context.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing calibration record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated calibration record.</returns>
    public async Task<Calibration> UpdateAsync(Guid id, Calibration model, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        CalibrationMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a calibration record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        _context.Calibrations.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts calibration records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets calibration records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of calibrations.</returns>
    public async Task<IEnumerable<Calibration>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.Calibrations
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a calibration record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.Calibrations
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Gets the timestamp of the latest calibration record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Gets the timestamp of the oldest calibration record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The oldest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsNoTracking().AsQueryable();
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        return await query.MinAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// Deletes all calibration records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        return await _context.Calibrations
            .Where(e => e.DataSource == source)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes all calibration records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Exclusive end, or null for no upper bound.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await query.ExecuteDeleteAsync(ct);
    }
}
