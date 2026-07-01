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
/// Repository for managing blood glucose check records in the database. A DeduplicationService
/// participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (extended <c>GetAsync</c> with the non-primary LinkedRecords filter, dedup
/// <c>BulkCreateAsync</c>). Soft-deletes inherit the base's audited path.
/// </summary>
public class BGCheckRepository : V4RepositoryBase<BGCheck, BGCheckEntity>, IBGCheckRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<BGCheckRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BGCheckRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    public BGCheckRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<BGCheckRepository> logger,
        IV4RecordBroadcaster<BGCheck>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override BGCheckEntity ToEntity(BGCheck model) => BGCheckMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BGCheck ToDomain(BGCheckEntity entity) => BGCheckMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BGCheckEntity target, BGCheck source) => BGCheckMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/>
    /// matches the rows <c>GetAsync</c> returns. Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<BGCheckEntity> ApplyReadVisibility(IQueryable<BGCheckEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "bgcheck" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Routes the base 7-arg form through the extended BG-check query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<BGCheck>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending, nativeOnly: false, ct);

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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.BGChecks.AsNoTracking().AsQueryable();
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
            .Any(lr => lr.RecordType == "bgcheck" && !lr.IsPrimary && lr.RecordId == b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .BGChecks.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
    }

    /// <summary>
    /// Insert-time deduplication: link saved records to canonical groups (runs after commit).
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<BGCheckEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { GlucoseValue = e.Glucose, GlucoseTolerance = 1.0 }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.BGCheck, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "BGCheck", inserted.Count);
        }
    }
}
