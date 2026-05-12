using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing blood glucose check records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class BGCheckRepository : IBGCheckRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<BGCheckRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BGCheckRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="logger">The logger instance.</param>
    public BGCheckRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        ILogger<BGCheckRepository> logger)
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <summary>
    /// Gets blood glucose check records based on filter criteria.
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
    /// <returns>A collection of blood glucose checks.</returns>
    public async Task<IEnumerable<BGCheck>> GetAsync(
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
        var query = _context.BGChecks.AsNoTracking().AsQueryable();
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
        query = query.Where(b => !_context.LinkedRecords
            .Any(lr => lr.RecordType == "bgcheck" && !lr.IsPrimary && lr.RecordId == b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a blood glucose check record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The blood glucose check, or null if not found.</returns>
    public async Task<BGCheck?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.BGChecks.FindAsync([id], ct);
        return entity is null ? null : BGCheckMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a blood glucose check record by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The blood glucose check, or null if not found.</returns>
    public async Task<BGCheck?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.BGChecks.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : BGCheckMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new blood glucose check record.
    /// </summary>
    /// <param name="model">The blood glucose check to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created blood glucose check.</returns>
    public async Task<BGCheck> CreateAsync(BGCheck model, CancellationToken ct = default)
    {
        var entity = BGCheckMapper.ToEntity(model);
        _context.BGChecks.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BGCheckMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing blood glucose check record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated blood glucose check.</returns>
    public async Task<BGCheck> UpdateAsync(Guid id, BGCheck model, CancellationToken ct = default)
    {
        var entity =
            await _context.BGChecks.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BGCheck {id} not found");
        BGCheckMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BGCheckMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a blood glucose check record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.BGChecks.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BGCheck {id} not found");
        _context.BGChecks.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts blood glucose check records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.BGChecks.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets blood glucose check records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of blood glucose checks.</returns>
    public async Task<IEnumerable<BGCheck>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .BGChecks.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a blood glucose check record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.BGChecks.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Performs a bulk creation of blood glucose check records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<BGCheck>> BulkCreateAsync(
        IEnumerable<BGCheck> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(BGCheckMapper.ToEntity).ToList();
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
                .BGChecks.AsNoTracking()
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
            _context.BGChecks.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // Insert-time deduplication: link saved records to canonical groups
        try
        {
            var dedupInputs = entities.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { GlucoseValue = e.Glucose, GlucoseTolerance = 1.0 }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.BGCheck, dedupInputs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "BGCheck", entities.Count);
        }

        return entities.Select(BGCheckMapper.ToDomainModel);
    }
}
