using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing sensor glucose (CGM) records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class SensorGlucoseRepository : ISensorGlucoseRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<SensorGlucoseRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorGlucoseRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public SensorGlucoseRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<SensorGlucoseRepository> logger
    )
    {
        _context = context;
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
        CancellationToken ct = default
    )
    {
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
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
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(b => !nonPrimaryIds.Contains(b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
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
        var entity = await _context.SensorGlucose.FindAsync([id], ct);
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
        var entity = await _context.SensorGlucose.FirstOrDefaultAsync(
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
        var entity = SensorGlucoseMapper.ToEntity(model);
        _context.SensorGlucose.Add(entity);
        await _context.SaveChangesAsync(ct);
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
        var entity =
            await _context.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        SensorGlucoseMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a sensor glucose record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        _context.SensorGlucose.Remove(entity);
        await _context.SaveChangesAsync(ct);
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
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
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
        var entities = await _context
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
        return await _context.AuditedExecuteDeleteAsync(
            _context.SensorGlucose.Where(e => e.LegacyId == legacyId), _auditContext, ct);
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
        var entities = records.Select(SensorGlucoseMapper.ToEntity).ToList();
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

        if (legacyIds.Count > 0)
        {
            var existingIds = await _context
                .SensorGlucose.AsNoTracking()
                .Where(e => legacyIds.Contains(e.LegacyId!))
                .Select(e => e.LegacyId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                .ToList();
        }

        if (entities.Count == 0)
            return [];

        const int batchSize = 500;
        foreach (var batch in entities.Chunk(batchSize))
        {
            _context.SensorGlucose.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // Insert-time deduplication: link saved records to canonical groups
        foreach (var entity in entities)
        {
            try
            {
                var criteria = new MatchCriteria
                {
                    GlucoseValue = entity.Mgdl,
                    GlucoseTolerance = 1.0
                };

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.SensorGlucose,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    criteria,
                    ct);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.SensorGlucose,
                    entity.Id,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    entity.DataSource ?? "unknown",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate SensorGlucose {Id}", entity.Id);
            }
        }

        return entities.Select(SensorGlucoseMapper.ToDomainModel);
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
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
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
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
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
        return await _context.SensorGlucose
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
        return await _context.AuditedExecuteDeleteAsync(
            _context.SensorGlucose.Where(e => e.DataSource == source), _auditContext, ct);
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
        var query = _context.SensorGlucose.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await _context.AuditedExecuteDeleteAsync(query, _auditContext, ct);
    }
}
