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
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing bolus records. A DeduplicationService participant on top of the sync-key
/// upsert and keyed delete of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only
/// the extended <c>GetAsync</c> (non-primary LinkedRecords filter + keyset cursor), the read-visibility
/// filter behind <c>CountAsync</c>, and the post-commit dedup linking.
/// </summary>
public class BolusRepository : SyncUpsertRepositoryBase<Bolus, BolusEntity>, IBolusRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<BolusRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BolusRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public BolusRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<BolusRepository> logger,
        IV4RecordBroadcaster<Bolus>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override BolusEntity ToEntity(Bolus model) => BolusMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Bolus ToDomain(BolusEntity entity) => BolusMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusEntity target, Bolus source) => BolusMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="CountAsync"/> matches the rows
    /// <c>GetAsync</c> returns. Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<BolusEntity> ApplyReadVisibility(IQueryable<BolusEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "bolus" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Routes the base 7-arg form through the extended bolus query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<Bolus>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, kind: null, afterTimestamp: null, afterId: null, ct);

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
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
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
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Boluses.AsNoTracking().AsQueryable();
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
        query = query.Where(b => !ctx.LinkedRecords
            .Any(lr => lr.RecordType == "bolus" && !lr.IsPrimary && lr.RecordId == b.Id));

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
        return entities.Select(BolusMapper.ToDomainModel);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .Boluses.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BolusMapper.ToDomainModel);
    }

    /// <summary>
    /// Insert-time deduplication runs AFTER commit: the ingested rows are durably persisted first, and
    /// dedup linking is best-effort (a failure is logged and healed by the reconcile service, not allowed
    /// to roll back the insert). Only runs on newly inserted entities — updated-in-place rows were already
    /// linked when first inserted.
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<BolusEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? DeduplicationInput.UnknownDataSource,
                Criteria: MatchCriteriaMapper.From(e)
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.Bolus, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "Bolus", inserted.Count);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bolus>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<BolusEntity>(from, to, limit, ct);
        return entities.Select(BolusMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<BolusEntity>(patientDeviceIdByRecordId, ct);
    }
}
