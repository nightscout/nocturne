using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing calibration records in the database.
/// </summary>
public class CalibrationRepository : ICalibrationRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<CalibrationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public CalibrationRepository(ITenantDbContextFactory contextFactory, ILogger<CalibrationRepository> logger)
    {
        _contextFactory = contextFactory;
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Calibrations.FindAsync([id], ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Calibrations.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = CalibrationMapper.ToEntity(model);
        ctx.Calibrations.Add(entity);
        await ctx.SaveChangesAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        CalibrationMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a calibration record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Calibration> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Calibrations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted Calibration {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Calibration>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.Calibrations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Calibration>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.Calibrations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.Calibrations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.Calibrations
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.Calibrations
            .Where(e => e.LegacyId == legacyId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Gets the timestamp of the latest calibration record.
    /// </summary>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest timestamp, or null if no records found.</returns>
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.Calibrations
            .Where(e => e.DataSource == source)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Calibration>> BulkCreateAsync(
        IEnumerable<Calibration> records,
        CancellationToken ct = default)
    {
        var entities = records.Select(CalibrationMapper.ToEntity).ToList();
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
                var existingRecords = await ctx.Calibrations.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => legacyIds.Contains(e.LegacyId!))
                    .Select(e => new { e.LegacyId, IsSoftDeleted = e.DeletedAt != null })
                    .ToListAsync(ct);

                var existingSet = existingRecords.Select(r => r.LegacyId).ToHashSet();
                var softDeletedCount = existingRecords.Count(r => r.IsSoftDeleted);

                if (softDeletedCount > 0)
                    _logger.LogInformation(
                        "Skipped {Count} previously-deleted {Type} records during import",
                        softDeletedCount, "Calibration");

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
                ctx.Calibrations.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            return entities.Select(CalibrationMapper.ToDomainModel);
        });
    }
}
