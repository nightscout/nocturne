using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing meter glucose (fingerstick) records in the database. A LegacyId-only type
/// (no SyncId-upsert, no DeduplicationService participation), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged plus the meter-glucose-specific
/// queries below.
/// </summary>
public class MeterGlucoseRepository : V4RepositoryBase<MeterGlucose, MeterGlucoseEntity>, IMeterGlucoseRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MeterGlucoseRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public MeterGlucoseRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<MeterGlucoseRepository> logger)
        : base(contextFactory, auditContext)
    {
    }

    /// <inheritdoc />
    protected override MeterGlucoseEntity ToEntity(MeterGlucose model) => MeterGlucoseMapper.ToEntity(model);

    /// <inheritdoc />
    protected override MeterGlucose ToDomain(MeterGlucoseEntity entity) => MeterGlucoseMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(MeterGlucoseEntity target, MeterGlucose source) =>
        MeterGlucoseMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets meter glucose records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of meter glucose records.</returns>
    public async Task<IEnumerable<MeterGlucose>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.MeterGlucose
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(MeterGlucoseMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes all meter glucose records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.MeterGlucose
            .Where(e => e.DataSource == source)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Deletes all meter glucose records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Exclusive end, or null for no upper bound.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.MeterGlucose.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }
}
