using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing sensor glucose (CGM) records in the database. A SyncId-upsert (bulk) +
/// DeduplicationService participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (extended <c>GetAsync</c> with the non-primary LinkedRecords filter + keyset cursor,
/// SyncId-upsert <c>BulkCreateAsync</c>, audited soft-deletes, and source/time-range deletes).
/// </summary>
public class SensorGlucoseRepository : V4RepositoryBase<SensorGlucose, SensorGlucoseEntity>, ISensorGlucoseRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<SensorGlucoseRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorGlucoseRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public SensorGlucoseRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<SensorGlucoseRepository> logger,
        IV4RecordBroadcaster<SensorGlucose>? broadcaster = null
    )
        : base(contextFactory, auditContext, broadcaster)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override SensorGlucoseEntity ToEntity(SensorGlucose model) => SensorGlucoseMapper.ToEntity(model);

    /// <inheritdoc />
    protected override SensorGlucose ToDomain(SensorGlucoseEntity entity) => SensorGlucoseMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(SensorGlucoseEntity target, SensorGlucose source) => SensorGlucoseMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/>
    /// matches the rows <c>GetAsync</c> returns. Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<SensorGlucoseEntity> ApplyReadVisibility(IQueryable<SensorGlucoseEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Routes the base 7-arg form through the extended sensor-glucose query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<SensorGlucose>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, afterTimestamp: null, afterId: null, ct);

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
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
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
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsNoTracking().AsQueryable();
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
            .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == b.Id));

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
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Creates a new sensor glucose record. When <c>DataSource</c> and <c>SyncIdentifier</c>
    /// match an existing row for this tenant, the record is updated in place rather than
    /// inserted — making the operation idempotent for connector replays. Tenant scoping is
    /// implicit via the DbContext's RLS-equivalent query filter. Mirrors BolusRepository.
    /// </summary>
    /// <param name="model">The sensor glucose record to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated sensor glucose record.</returns>
    public override async Task<SensorGlucose> CreateAsync(SensorGlucose model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await ctx.SensorGlucose
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                SensorGlucoseMapper.UpdateEntity(existing, model);
                await ctx.SaveChangesAsync(ct);
                var upserted = SensorGlucoseMapper.ToDomainModel(existing);
                // A single explicit upsert always broadcasts (no material-change gate on the single path).
                await RaiseBroadcastAsync([], [upserted], [], origin, ct);
                return upserted;
            }
        }

        var entity = SensorGlucoseMapper.ToEntity(model);
        ctx.SensorGlucose.Add(entity);
        await ctx.SaveChangesAsync(ct);
        var created = SensorGlucoseMapper.ToDomainModel(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .SensorGlucose.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place — so timezone re-correction moves a reading's
    /// timestamp instead of duplicating it. Persists the updates inside the transaction before returning
    /// so the base's insert loop (which clears the tracker) doesn't lose them. Mirrors BolusRepository.
    /// </summary>
    protected override async Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<SensorGlucoseEntity> entities, CancellationToken ct)
    {
        // Intra-batch SyncIdentifier dedup: keep last occurrence per (DataSource, SyncIdentifier).
        // Records without both keys keep a unique grouping key so they're not collapsed.
        entities = entities
            .GroupBy(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier)
                ? $"sync|{e.DataSource}|{e.SyncIdentifier}"
                : $"id|{e.Id}")
            .Select(g => g.Last())
            .ToList();

        // DB-level SyncIdentifier upsert: rows matched by (DataSource, SyncIdentifier) are updated
        // in place. Everything else falls through to the LegacyId/insert path below.
        var updatedEntities = new List<SensorGlucoseEntity>();
        var materiallyChanged = new List<SensorGlucoseEntity>();
        var syncKeyed = entities
            .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
            .ToList();

        if (syncKeyed.Count == 0)
            return new UpsertSplit(updatedEntities, materiallyChanged, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        var existingRows = await ctx.SensorGlucose.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId)
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.First());

        var toInsert = new List<SensorGlucoseEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                var domain = SensorGlucoseMapper.ToDomainModel(entity);
                SensorGlucoseMapper.UpdateEntity(existing, domain);
                updatedEntities.Add(existing);
                // Capture material changes now, before SaveChanges clears the modified flags.
                if (HasMaterialChange(ctx, existing))
                    materiallyChanged.Add(existing);
            }
            else
            {
                toInsert.Add(entity);
            }
        }

        if (updatedEntities.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return new UpsertSplit(updatedEntities, materiallyChanged, toInsert);
    }

    /// <summary>
    /// Insert-time deduplication: link newly inserted records to canonical groups. Feeds inserts-only
    /// (matching Bolus/CarbIntake). Since dedup runs after commit (D4), upserted rows are reached as
    /// committed canonicals via their committed value, so re-feeding them is redundant — already-linked
    /// rows skip self-relinking and add no match candidates.
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<SensorGlucoseEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0) return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { GlucoseValue = e.Mgdl, GlucoseTolerance = 1.0 }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.SensorGlucose, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "SensorGlucose", inserted.Count);
        }
    }

    /// <summary>
    /// Counts sensor glucose records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of matching records.</returns>
    public async Task<int> CountBySourceAsync(string source, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SensorGlucose
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.Where(e => e.DataSource == source), AuditContext, ct);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.SensorGlucose.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await ctx.AuditedSoftDeleteAsync(query, AuditContext, ct);
    }
}
