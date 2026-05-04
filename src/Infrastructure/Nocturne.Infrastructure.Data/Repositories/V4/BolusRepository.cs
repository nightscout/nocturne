using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing bolus records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class BolusRepository : IBolusRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<BolusRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BolusRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public BolusRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<BolusRepository> logger)
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets bolus records based on filter criteria.
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
    /// <param name="kind">Optional bolus kind filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of bolus records.</returns>
    public async Task<IEnumerable<Bolus>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        BolusKind? kind = null,
        CancellationToken ct = default
    )
    {
        var query = _context.Boluses.AsNoTracking().AsQueryable();
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
        if (kind.HasValue)
            query = query.Where(e => e.BolusKind == kind.Value.ToString());

        // Exclude non-primary duplicates from cross-connector deduplication
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "bolus" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(b => !nonPrimaryIds.Contains(b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BolusMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a bolus record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The bolus record, or null if not found.</returns>
    public async Task<Bolus?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Boluses.FindAsync([id], ct);
        return entity is null ? null : BolusMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a bolus record by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The bolus record, or null if not found.</returns>
    public async Task<Bolus?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.Boluses.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : BolusMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new bolus record. When <c>DataSource</c> and <c>SyncIdentifier</c>
    /// match an existing row for this tenant, the record is updated in place rather
    /// than inserted — making the operation idempotent for connector replays.
    /// Tenant scoping is implicit via the DbContext's RLS-equivalent query filter.
    /// </summary>
    /// <param name="model">The bolus to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated bolus record.</returns>
    public async Task<Bolus> CreateAsync(Bolus model, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await _context.Boluses
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                BolusMapper.UpdateEntity(existing, model);
                await _context.SaveChangesAsync(ct);
                return BolusMapper.ToDomainModel(existing);
            }
        }

        var entity = BolusMapper.ToEntity(model);
        _context.Boluses.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BolusMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing bolus record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated bolus record.</returns>
    public async Task<Bolus> UpdateAsync(Guid id, Bolus model, CancellationToken ct = default)
    {
        var entity =
            await _context.Boluses.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Bolus {id} not found");
        BolusMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BolusMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a bolus record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.Boluses.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Bolus {id} not found");
        _context.Boluses.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts bolus records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.Boluses.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets bolus records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of bolus records.</returns>
    public async Task<IEnumerable<Bolus>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .Boluses.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BolusMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a bolus record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.Boluses.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Deletes bolus records matching the given data source and sync identifier.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.Boluses.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            _auditContext, ct);
    }

    /// <summary>
    /// Performs a bulk creation of bolus records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<Bolus>> BulkCreateAsync(
        IEnumerable<Bolus> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(BolusMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

        // Intra-batch SyncIdentifier dedup: keep last occurrence per
        // (DataSource, SyncIdentifier). Records without both keys keep a
        // unique grouping key so they're not collapsed.
        entities = entities
            .GroupBy(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier)
                ? $"sync|{e.DataSource}|{e.SyncIdentifier}"
                : $"id|{e.Id}")
            .Select(g => g.Last())
            .ToList();

        // DB-level SyncIdentifier upsert: match any existing rows keyed by
        // (DataSource, SyncIdentifier) and update them in place. Everything
        // else falls through to the insert path below.
        var syncKeyed = entities
            .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
            .ToList();

        var updatedEntities = new List<BolusEntity>();
        if (syncKeyed.Count > 0)
        {
            var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
            var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

            // Over-fetches by a Cartesian amount; the partial unique index
            // on (tenant_id, data_source, sync_identifier) keeps this cheap.
            var existingRows = await _context.Boluses
                .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
                .ToListAsync(ct);

            var existingByKey = existingRows
                .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
                .ToDictionary(g => g.Key, g => g.First());

            var toInsert = new List<BolusEntity>();
            foreach (var entity in entities)
            {
                var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                    && !string.IsNullOrEmpty(entity.SyncIdentifier);
                if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
                {
                    // Update in place — mirror the single-record CreateAsync path via the mapper.
                    var domain = BolusMapper.ToDomainModel(entity);
                    BolusMapper.UpdateEntity(existing, domain);
                    updatedEntities.Add(existing);
                }
                else
                {
                    toInsert.Add(entity);
                }
            }

            if (updatedEntities.Count > 0)
            {
                // Persist updates before the insert-chunking loop clears the tracker.
                await _context.SaveChangesAsync(ct);
            }

            entities = toInsert;
        }

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
                .Boluses.AsNoTracking()
                .Where(e => legacyIds.Contains(e.LegacyId!))
                .Select(e => e.LegacyId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                .ToList();
        }

        if (entities.Count > 0)
        {
            const int batchSize = 500;
            foreach (var batch in entities.Chunk(batchSize))
            {
                _context.Boluses.AddRange(batch);
                await _context.SaveChangesAsync(ct);
                _context.ChangeTracker.Clear();
            }

            // Insert-time deduplication: link saved records to canonical groups.
            // Only runs on newly inserted entities — updated-in-place rows were
            // already linked when first inserted.
            foreach (var entity in entities)
            {
                try
                {
                    var criteria = new MatchCriteria
                    {
                        Insulin = entity.Insulin,
                        InsulinTolerance = 0.05
                    };

                    var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                        RecordType.Bolus,
                        new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        criteria,
                        ct);

                    await _deduplicationService.LinkRecordAsync(
                        canonicalId,
                        RecordType.Bolus,
                        entity.Id,
                        new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        entity.DataSource ?? "unknown",
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deduplicate Bolus {Id}", entity.Id);
                }
            }
        }

        return updatedEntities.Concat(entities).Select(BolusMapper.ToDomainModel);
    }
}
