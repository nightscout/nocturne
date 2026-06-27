using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing note records in the database. A DeduplicationService participant, so it
/// inherits the shared CRUD/soft-delete surface from <see cref="V4RepositoryBase{TModel,TEntity}"/>
/// and keeps only the dedup-specific behaviour as overrides (extended <c>GetAsync</c> with the
/// non-primary LinkedRecords filter, dedup <c>BulkCreateAsync</c>). Soft-deletes use the plain
/// (non-audited) path, matching the base.
/// </summary>
public class NoteRepository : V4RepositoryBase<Note, NoteEntity>, INoteRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<NoteRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoteRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="logger">The logger instance.</param>
    public NoteRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        ILogger<NoteRepository> logger)
        : base(contextFactory)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override NoteEntity ToEntity(Note model) => NoteMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Note ToDomain(NoteEntity entity) => NoteMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(NoteEntity target, Note source) => NoteMapper.UpdateEntity(target, source);

    /// <summary>
    /// Routes the base 7-arg form through the extended note query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<Note>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending, nativeOnly: false, ct);

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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Notes.AsNoTracking().AsQueryable();
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
        query = query.Where(b => !ctx.LinkedRecords
            .Any(lr => lr.RecordType == "note" && !lr.IsPrimary && lr.RecordId == b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .Notes.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.Notes.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Performs a bulk creation of note records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created notes.</returns>
    public override async Task<IEnumerable<Note>> BulkCreateAsync(
        IEnumerable<Note> records,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            var entities = records.Select(NoteMapper.ToEntity).ToList();
            if (entities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
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
                var blockedLegacyIds = await ctx.GetBlockingLegacyIdsAsync<NoteEntity>(legacyIds, ct);

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !blockedLegacyIds.Contains(e.LegacyId))
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
                ctx.Notes.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);

            // Insert-time deduplication: link saved records to canonical groups
            try
            {
                var dedupInputs = entities.Select(e => new DeduplicationInput(
                    RecordId: e.Id,
                    Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    DataSource: e.DataSource ?? "unknown",
                    Criteria: new MatchCriteria()
                )).ToList();

                await _deduplicationService.DeduplicateBatchAsync(RecordType.Note, dedupInputs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "Note", entities.Count);
            }

            return entities.Select(NoteMapper.ToDomainModel);
        });
    }
}
