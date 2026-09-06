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
/// Repository for managing uploader snapshot records (point-in-time uploader state) in the database.
/// Takes the sync-key upsert and keyed delete of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>,
/// so it keeps only the uploader-specific queries.
/// </summary>
public class UploaderSnapshotRepository : SyncUpsertRepositoryBase<UploaderSnapshot, UploaderSnapshotEntity>, IUploaderSnapshotRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploaderSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    // logger is unused but retained for DI + direct test construction.
    public UploaderSnapshotRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<UploaderSnapshotRepository> logger,
        IV4RecordBroadcaster<UploaderSnapshot>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override UploaderSnapshotEntity ToEntity(UploaderSnapshot model) => UploaderSnapshotMapper.ToEntity(model);

    /// <inheritdoc />
    protected override UploaderSnapshot ToDomain(UploaderSnapshotEntity entity) => UploaderSnapshotMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(UploaderSnapshotEntity target, UploaderSnapshot source) =>
        UploaderSnapshotMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets uploader snapshots by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching uploader snapshots.</returns>
    public async Task<IEnumerable<UploaderSnapshot>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.UploaderSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(UploaderSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<UploaderSnapshot?> GetLatestAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.UploaderSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var entity = await query
            .OrderBy(e => e.Battery == null)        // false (has battery) before true (null) — nulls last
            .ThenBy(e => e.Battery)                 // lowest battery first
            .ThenByDescending(e => e.Timestamp)     // tie-break: most recent
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : UploaderSnapshotMapper.ToDomainModel(entity);
    }
}
