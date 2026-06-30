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
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing carbohydrate intake records in the database. A SyncId-upsert +
/// DeduplicationService participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (extended <c>GetAsync</c> with the non-primary LinkedRecords filter + keyset cursor,
/// SyncId-upsert <c>CreateAsync</c>/<c>BulkCreateAsync</c>, the non-primary-excluding <c>CountAsync</c>,
/// and audited soft-deletes).
/// </summary>
public class CarbIntakeRepository : V4RepositoryBase<CarbIntake, CarbIntakeEntity>, ICarbIntakeRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<CarbIntakeRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CarbIntakeRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public CarbIntakeRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<CarbIntakeRepository> logger)
        : base(contextFactory, auditContext)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override CarbIntakeEntity ToEntity(CarbIntake model) => CarbIntakeMapper.ToEntity(model);

    /// <inheritdoc />
    protected override CarbIntake ToDomain(CarbIntakeEntity entity) => CarbIntakeMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CarbIntakeEntity target, CarbIntake source) => CarbIntakeMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/>
    /// matches the rows <c>GetAsync</c> returns (otherwise pagination totals are inflated by duplicate
    /// meals imported from multiple connectors). Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<CarbIntakeEntity> ApplyReadVisibility(IQueryable<CarbIntakeEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "carbintake" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Routes the base 7-arg form through the extended carb-intake query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<CarbIntake>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, afterTimestamp: null, afterId: null, ct);

    /// <summary>
    /// Gets carbohydrate intake records based on filter criteria.
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
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate intakes.</returns>
    public async Task<IEnumerable<CarbIntake>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.CarbIntakes.AsNoTracking().AsQueryable();
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
            .Any(lr => lr.RecordType == "carbintake" && !lr.IsPrimary && lr.RecordId == b.Id));

        // Keyset cursor — when provided, replaces OFFSET with a WHERE clause
        // that seeks directly to the cursor position. O(limit) vs O(offset + limit).
        if (afterTimestamp.HasValue && afterId.HasValue)
        {
            query = descending
                ? query.Where(e => e.Timestamp < afterTimestamp.Value
                    || (e.Timestamp == afterTimestamp.Value && e.Id < afterId.Value))
                : query.Where(e => e.Timestamp > afterTimestamp.Value
                    || (e.Timestamp == afterTimestamp.Value && e.Id > afterId.Value));
        }

        query = descending
            ? query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id);

        if (!afterTimestamp.HasValue || !afterId.HasValue)
        {
            query = query.Skip(offset);
        }

        var entities = await query.Take(limit).ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel);
    }

    /// <summary>
    /// Creates a new carbohydrate intake record. When <c>DataSource</c> and
    /// <c>SyncIdentifier</c> match an existing row for this tenant, the record is
    /// updated in place rather than inserted — making the operation idempotent
    /// for connector replays. Tenant scoping is implicit via the DbContext's
    /// RLS-equivalent query filter.
    /// </summary>
    /// <param name="model">The carbohydrate intake to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated carbohydrate intake.</returns>
    public override async Task<CarbIntake> CreateAsync(CarbIntake model, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await ctx.CarbIntakes
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                CarbIntakeMapper.UpdateEntity(existing, model);
                await ctx.SaveChangesAsync(ct);
                return CarbIntakeMapper.ToDomainModel(existing);
            }
        }

        var entity = CarbIntakeMapper.ToEntity(model);
        ctx.CarbIntakes.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return CarbIntakeMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets carbohydrate intake records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate intakes.</returns>
    public async Task<IEnumerable<CarbIntake>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .CarbIntakes.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes carbohydrate intake records matching the given data source and sync identifier.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.CarbIntakes.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            AuditContext, ct);
    }

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place. Persists the updates inside the transaction
    /// before returning so the base's insert loop (which clears the tracker) doesn't lose them.
    /// </summary>
    protected override async Task<(List<CarbIntakeEntity> Updated, List<CarbIntakeEntity> ToInsert)> SplitUpsertsAsync(
        NocturneDbContext ctx, List<CarbIntakeEntity> entities, CancellationToken ct)
    {
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

        var updatedEntities = new List<CarbIntakeEntity>();
        if (syncKeyed.Count == 0)
            return (updatedEntities, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        // Over-fetches by a Cartesian amount; the partial unique index
        // on (tenant_id, data_source, sync_identifier) keeps this cheap.
        var existingRows = await ctx.CarbIntakes.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId)
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.First());

        var toInsert = new List<CarbIntakeEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                // Update in place — mirror the single-record CreateAsync path via the mapper.
                var domain = CarbIntakeMapper.ToDomainModel(entity);
                CarbIntakeMapper.UpdateEntity(existing, domain);
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
            await ctx.SaveChangesAsync(ct);
        }

        return (updatedEntities, toInsert);
    }

    /// <summary>
    /// Insert-time deduplication runs AFTER commit: the ingested rows are durably persisted first, and
    /// dedup linking is best-effort (a failure is logged and healed by the reconcile service, not allowed
    /// to roll back the insert). Only runs on newly inserted entities — updated-in-place rows were already
    /// linked when first inserted.
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<CarbIntakeEntity> inserted, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { Carbs = e.Carbs, CarbsTolerance = 1.0 }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.CarbIntake, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "CarbIntake", inserted.Count);
        }
    }
}
