using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing pump snapshot records (point-in-time pump state) in the database. Takes the
/// sync-key upsert and keyed delete of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it
/// keeps only the pump-specific queries.
/// </summary>
public class PumpSnapshotRepository : SyncUpsertRepositoryBase<PumpSnapshot, PumpSnapshotEntity>, IPumpSnapshotRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PumpSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    // logger is unused but retained for DI + direct test construction.
    public PumpSnapshotRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<PumpSnapshotRepository> logger,
        IV4RecordBroadcaster<PumpSnapshot>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override PumpSnapshotEntity ToEntity(PumpSnapshot model) => PumpSnapshotMapper.ToEntity(model);

    /// <inheritdoc />
    protected override PumpSnapshot ToDomain(PumpSnapshotEntity entity) => PumpSnapshotMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(PumpSnapshotEntity target, PumpSnapshot source) =>
        PumpSnapshotMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<PumpSnapshot?> GetLatestBeforeAsync(DateTime timestamp, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.PumpSnapshots
            .AsNoTracking()
            .Where(e => e.Timestamp < timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<PumpSnapshot?> GetLatestAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.PumpSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets pump snapshots by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching pump snapshots.</returns>
    public async Task<IEnumerable<PumpSnapshot>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.PumpSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }
}
