using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing note records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class NoteRepository : INoteRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<NoteRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoteRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="logger">The logger instance.</param>
    public NoteRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        ILogger<NoteRepository> logger)
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <summary>
    /// Gets note records based on filter criteria.
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
    /// <returns>A collection of notes.</returns>
    public async Task<IEnumerable<Note>> GetAsync(
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
        var query = _context.Notes.AsNoTracking().AsQueryable();
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
            .Where(lr => lr.RecordType == "note" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(b => !nonPrimaryIds.Contains(b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a note record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The note record, or null if not found.</returns>
    public async Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FindAsync([id], ct);
        return entity is null ? null : NoteMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a note record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The note record, or null if not found.</returns>
    public async Task<Note?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : NoteMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new note record.
    /// </summary>
    /// <param name="model">The note to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created note record.</returns>
    public async Task<Note> CreateAsync(Note model, CancellationToken ct = default)
    {
        var entity = NoteMapper.ToEntity(model);
        _context.Notes.Add(entity);
        await _context.SaveChangesAsync(ct);
        return NoteMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing note record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated note record.</returns>
    public async Task<Note> UpdateAsync(Guid id, Note model, CancellationToken ct = default)
    {
        var entity =
            await _context.Notes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Note {id} not found");
        NoteMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return NoteMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a note record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.Notes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Note {id} not found");
        _context.Notes.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts note records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.Notes.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets note records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of notes.</returns>
    public async Task<IEnumerable<Note>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .Notes.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a note record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.Notes.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes note records matching the given data source and sync identifier.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        return await _context.Notes.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Performs a bulk creation of note records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created notes.</returns>
    public async Task<IEnumerable<Note>> BulkCreateAsync(
        IEnumerable<Note> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(NoteMapper.ToEntity).ToList();
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
                .Notes.AsNoTracking()
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
            _context.Notes.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // Insert-time deduplication: link saved records to canonical groups
        foreach (var entity in entities)
        {
            try
            {
                var criteria = new MatchCriteria();

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.Note,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    criteria,
                    ct);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.Note,
                    entity.Id,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    entity.DataSource ?? "unknown",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate Note {Id}", entity.Id);
            }
        }

        return entities.Select(NoteMapper.ToDomainModel);
    }
}
