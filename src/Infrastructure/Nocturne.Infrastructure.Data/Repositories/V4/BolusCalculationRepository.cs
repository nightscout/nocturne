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
/// Repository for managing bolus calculation records in the database. A deduplication
/// participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (the <c>GetAsync</c> with the duplicate filter, dedup
/// <c>BulkCreateAsync</c>, audited soft-deletes).
/// </summary>
public class BolusCalculationRepository : V4RepositoryBase<BolusCalculation, BolusCalculationEntity>, IBolusCalculationRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BolusCalculationRepository"/> class.
    /// </summary>
    public BolusCalculationRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<BolusCalculationRepository> logger,
        IV4RecordBroadcaster<BolusCalculation>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null
    )
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override BolusCalculationEntity ToEntity(BolusCalculation model) => BolusCalculationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BolusCalculation ToDomain(BolusCalculationEntity entity) => BolusCalculationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusCalculationEntity target, BolusCalculation source) => BolusCalculationMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets bolus calculation records based on filter criteria.
    /// Hides the non-primary copies of duplicate groups.
    /// Overrides the base 7-arg form to add the duplicate filter.
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

        query = query.ExcludeDuplicates();

        query = query.InObservationOrder(descending);
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
}
