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
/// Repository for managing temporary basal records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class TempBasalRepository : ITempBasalRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<TempBasalRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TempBasalRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public TempBasalRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<TempBasalRepository> logger)
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets temporary basal records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by start timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of temporary basal records.</returns>
    public async Task<IEnumerable<TempBasal>> GetAsync(
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
        var query = _context.TempBasals.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.StartTimestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.StartTimestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);

        // Exclude non-primary duplicates from cross-connector deduplication
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(b => !nonPrimaryIds.Contains(b.Id));

        query = descending
            ? query.OrderByDescending(e => e.StartTimestamp)
            : query.OrderBy(e => e.StartTimestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(TempBasalMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a temporary basal record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The temporary basal record, or null if not found.</returns>
    public async Task<TempBasal?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.TempBasals.FindAsync([id], ct);
        return entity is null ? null : TempBasalMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a temporary basal record by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The temporary basal record, or null if not found.</returns>
    public async Task<TempBasal?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.TempBasals.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : TempBasalMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new temporary basal record.
    /// </summary>
    /// <param name="model">The temporary basal record to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created temporary basal record.</returns>
    public async Task<TempBasal> CreateAsync(TempBasal model, CancellationToken ct = default)
    {
        var entity = TempBasalMapper.ToEntity(model);
        _context.TempBasals.Add(entity);
        await _context.SaveChangesAsync(ct);
        return TempBasalMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing temporary basal record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated temporary basal record.</returns>
    public async Task<TempBasal> UpdateAsync(Guid id, TempBasal model, CancellationToken ct = default)
    {
        var entity =
            await _context.TempBasals.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TempBasal {id} not found");
        TempBasalMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return TempBasalMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a temporary basal record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.TempBasals.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TempBasal {id} not found");
        _context.TempBasals.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a temporary basal record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.TempBasals.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Counts temporary basal records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.TempBasals.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.StartTimestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.StartTimestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Performs a bulk creation of temporary basal records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<TempBasal>> BulkCreateAsync(
        IEnumerable<TempBasal> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(TempBasalMapper.ToEntity).ToList();
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
                .TempBasals.AsNoTracking()
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
            _context.TempBasals.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // Cross-connector deduplication: link saved records to canonical groups
        foreach (var entity in entities)
        {
            try
            {
                var criteria = new MatchCriteria
                {
                    Rate = entity.Rate,
                    RateTolerance = 0.05
                };

                var mills = new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.TempBasal,
                    mills,
                    criteria,
                    ct);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.TempBasal,
                    entity.Id,
                    mills,
                    entity.DataSource ?? "unknown",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate TempBasal {Id}", entity.Id);
            }
        }

        return entities.Select(TempBasalMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes temporary basal records by data source and date range.
    /// </summary>
    /// <param name="source">The data source filter.</param>
    /// <param name="from">The start timestamp filter.</param>
    /// <param name="to">The end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySourceAndDateRangeAsync(
        string source,
        DateTime from,
        DateTime to,
        CancellationToken ct = default
    )
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.TempBasals.Where(e => e.DataSource == source && e.StartTimestamp >= from && e.StartTimestamp <= to),
            _auditContext, ct);
    }

    /// <inheritdoc />
    public async Task<TempBasal?> GetActiveAtAsync(DateTime at, CancellationToken ct = default)
    {
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);

        var entity = await _context.TempBasals
            .AsNoTracking()
            .Where(t => t.StartTimestamp <= at && (t.EndTimestamp == null || t.EndTimestamp > at))
            .Where(t => !nonPrimaryIds.Contains(t.Id))
            .OrderByDescending(t => t.StartTimestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : TempBasalMapper.ToDomainModel(entity);
    }
}
