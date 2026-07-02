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
/// Repository for managing bolus calculation records in the database. A DeduplicationService
/// participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (the <c>GetAsync</c> with the non-primary LinkedRecords filter, dedup
/// <c>BulkCreateAsync</c>, audited soft-deletes).
/// </summary>
public class BolusCalculationRepository : V4RepositoryBase<BolusCalculation, BolusCalculationEntity>, IBolusCalculationRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<BolusCalculationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BolusCalculationRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public BolusCalculationRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<BolusCalculationRepository> logger,
        IV4RecordBroadcaster<BolusCalculation>? broadcaster = null
    )
        : base(contextFactory, auditContext, broadcaster)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override BolusCalculationEntity ToEntity(BolusCalculation model) => BolusCalculationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BolusCalculation ToDomain(BolusCalculationEntity entity) => BolusCalculationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusCalculationEntity target, BolusCalculation source) => BolusCalculationMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/>
    /// matches the rows <c>GetAsync</c> returns. Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<BolusCalculationEntity> ApplyReadVisibility(IQueryable<BolusCalculationEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "boluscalculation" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Gets bolus calculation records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
    /// Overrides the base 7-arg form to add the non-primary LinkedRecords exclusion.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of bolus calculations.</returns>
    public override async Task<IEnumerable<BolusCalculation>> GetAsync(
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.BolusCalculations.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);

        // Exclude non-primary duplicates from cross-connector deduplication
        query = query.Where(b => !ctx.LinkedRecords
            .Any(lr => lr.RecordType == "boluscalculation" && !lr.IsPrimary && lr.RecordId == b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets bolus calculation records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of bolus calculations.</returns>
    public async Task<IEnumerable<BolusCalculation>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .BolusCalculations.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    /// <summary>
    /// Insert-time deduplication: link saved records to canonical groups (runs after commit).
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<BolusCalculationEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { Carbs = e.CarbInput ?? 0, CarbsTolerance = 1.0 }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.BolusCalculation, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "BolusCalculation", inserted.Count);
        }
    }
}
