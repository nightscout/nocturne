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
/// Repository for managing calibration records. A LegacyId-only type (no SyncId-upsert, no
/// DeduplicationService participation), so it uses the shared <see cref="V4RepositoryBase{TModel,TEntity}"/>
/// behaviour unchanged plus the calibration-specific queries below.
/// </summary>
public class CalibrationRepository : V4RepositoryBase<Calibration, CalibrationEntity>, ICalibrationRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public CalibrationRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<CalibrationRepository> logger)
        : base(contextFactory, auditContext)
    {
    }

    /// <inheritdoc />
    protected override CalibrationEntity ToEntity(Calibration model) => CalibrationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Calibration ToDomain(CalibrationEntity entity) => CalibrationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CalibrationEntity target, Calibration source) =>
        CalibrationMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets calibration records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of calibrations.</returns>
    public async Task<IEnumerable<Calibration>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Calibrations
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes all calibration records for the given data source.
    /// </summary>
    /// <param name="source">Data source identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.Calibrations
            .Where(e => e.DataSource == source)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Deletes all calibration records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Exclusive end, or null for no upper bound.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp < to.Value);

        return await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }
}
