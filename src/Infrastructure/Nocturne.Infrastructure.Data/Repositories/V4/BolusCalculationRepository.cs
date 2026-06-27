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
/// Repository for managing bolus calculation records in the database. A DeduplicationService
/// participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (the <c>GetAsync</c> with the non-primary LinkedRecords filter, dedup
/// <c>BulkCreateAsync</c>, audited soft-deletes).
/// </summary>
public class BolusCalculationRepository : V4RepositoryBase<BolusCalculation, BolusCalculationEntity>, IBolusCalculationRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
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
        ILogger<BolusCalculationRepository> logger
    )
        : base(contextFactory)
    {
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override BolusCalculationEntity ToEntity(BolusCalculation model) => BolusCalculationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BolusCalculation ToDomain(BolusCalculationEntity entity) => BolusCalculationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusCalculationEntity target, BolusCalculation source) => BolusCalculationMapper.UpdateEntity(target, source);

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
    /// Deletes a bolus calculation record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public override async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.BolusCalculations.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Performs a bulk creation of bolus calculation records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public override async Task<IEnumerable<BolusCalculation>> BulkCreateAsync(
        IEnumerable<BolusCalculation> records,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            var entities = records.Select(BolusCalculationMapper.ToEntity).ToList();
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
                var blockedLegacyIds = await ctx.GetBlockingLegacyIdsAsync<BolusCalculationEntity>(legacyIds, ct);

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
                ctx.BolusCalculations.AddRange(batch);
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
                    Criteria: new MatchCriteria { Carbs = e.CarbInput ?? 0, CarbsTolerance = 1.0 }
                )).ToList();

                await _deduplicationService.DeduplicateBatchAsync(RecordType.BolusCalculation, dedupInputs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "BolusCalculation", entities.Count);
            }

            return entities.Select(BolusCalculationMapper.ToDomainModel);
        });
    }
}
