using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing bolus calculation records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class BolusCalculationRepository : IBolusCalculationRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
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
    {
        _contextFactory = contextFactory;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets bolus calculation records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
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
    public async Task<IEnumerable<BolusCalculation>> GetAsync(
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
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
    /// Gets a bolus calculation record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The bolus calculation, or null if not found.</returns>
    public async Task<BolusCalculation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.BolusCalculations.FindAsync([id], ct);
        return entity is null ? null : BolusCalculationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a bolus calculation record by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The bolus calculation, or null if not found.</returns>
    public async Task<BolusCalculation?> GetByLegacyIdAsync(
        string legacyId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.BolusCalculations.FirstOrDefaultAsync(
            e => e.LegacyId == legacyId,
            ct
        );
        return entity is null ? null : BolusCalculationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new bolus calculation record.
    /// </summary>
    /// <param name="model">The bolus calculation to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created bolus calculation.</returns>
    public async Task<BolusCalculation> CreateAsync(
        BolusCalculation model,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = BolusCalculationMapper.ToEntity(model);
        ctx.BolusCalculations.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return BolusCalculationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing bolus calculation record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated bolus calculation.</returns>
    public async Task<BolusCalculation> UpdateAsync(
        Guid id,
        BolusCalculation model,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.BolusCalculations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BolusCalculation {id} not found");
        BolusCalculationMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return BolusCalculationMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a bolus calculation record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.BolusCalculations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BolusCalculation {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<BolusCalculation> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.BolusCalculations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted BolusCalculation {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return BolusCalculationMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BolusCalculation>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.BolusCalculations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BolusCalculation>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.BolusCalculations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.BolusCalculations.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Counts bolus calculation records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.BolusCalculations.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
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
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.BolusCalculations.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Performs a bulk creation of bolus calculation records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<BolusCalculation>> BulkCreateAsync(
        IEnumerable<BolusCalculation> records,
        CancellationToken ct = default
    )
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
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
                var existingRecords = await ctx.BolusCalculations.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => legacyIds.Contains(e.LegacyId!))
                    .Select(e => new { e.LegacyId, IsSoftDeleted = e.DeletedAt != null })
                    .ToListAsync(ct);

                var existingSet = existingRecords.Select(r => r.LegacyId).ToHashSet();
                var softDeletedCount = existingRecords.Count(r => r.IsSoftDeleted);

                if (softDeletedCount > 0)
                    _logger.LogInformation(
                        "Skipped {Count} previously-deleted {Type} records during import",
                        softDeletedCount, "BolusCalculation");

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
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
