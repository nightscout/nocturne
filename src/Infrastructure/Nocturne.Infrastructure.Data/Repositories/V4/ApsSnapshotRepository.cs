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
/// Repository for managing APS snapshots in the database. Takes the sync-key upsert and keyed delete
/// of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only the APS-specific
/// queries.
/// </summary>
public class ApsSnapshotRepository : SyncUpsertRepositoryBase<ApsSnapshot, ApsSnapshotEntity>, IApsSnapshotRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApsSnapshotRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    // logger is unused but retained for DI + direct test construction.
    public ApsSnapshotRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<ApsSnapshotRepository> logger,
        IV4RecordBroadcaster<ApsSnapshot>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override ApsSnapshotEntity ToEntity(ApsSnapshot model) => ApsSnapshotMapper.ToEntity(model);

    /// <inheritdoc />
    protected override ApsSnapshot ToDomain(ApsSnapshotEntity entity) => ApsSnapshotMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(ApsSnapshotEntity target, ApsSnapshot source) =>
        ApsSnapshotMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApsIobCobPoint>> GetIobCobPointsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.ApsSnapshots.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .Select(e => new ApsIobCobPoint(e.Timestamp, e.Iob, e.Cob))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets APS snapshots by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching APS snapshots.</returns>
    public async Task<IEnumerable<ApsSnapshot>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ApsSnapshots
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets APS snapshots modified since the given timestamp, ordered oldest-first.
    /// </summary>
    /// <param name="lastModifiedMills">Unix millisecond timestamp threshold.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching APS snapshots ordered by modification time ascending.</returns>
    public async Task<IEnumerable<ApsSnapshot>> GetModifiedSinceAsync(
        long lastModifiedMills, int limit = 1000, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;
        // Filter and order on the event Timestamp: it is the clock the V3 devicestatus DTO
        // reports as srvModified and the AAPS history cursor advances on, and it is the
        // indexed column. Filtering on the write clock (SysUpdatedAt) instead sets the cursor
        // below the returned rows' write time, so every poll re-matches them (an incremental-
        // sync loop). Strictly-greater (not >=) so the cursor record AAPS already holds is not
        // re-returned; the boundary record's sub-millisecond remainder is deduplicated by AAPS
        // rather than dropped (a >= cursor+1ms bound would silently skip sub-ms page splits).
        var entities = await ctx.ApsSnapshots
            .AsNoTracking()
            .Where(e => e.Timestamp > since)
            .OrderBy(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsOfAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking();
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestEnactedTimestampAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().Where(e => e.Enacted);
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Non-finite (Infinity/NaN) values from corrupt connector payloads are coerced to null rather than throwing.
    /// </remarks>
    public async Task<decimal?> GetLatestSensitivityRatioAsync(DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.ApsSnapshots.AsNoTracking().Where(e => e.SensitivityRatio != null);
        if (asOf.HasValue) query = query.Where(e => e.Timestamp <= asOf.Value);
        var value = await query
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.SensitivityRatio)
            .FirstOrDefaultAsync(ct);
        return value is double v && double.IsFinite(v) ? (decimal)v : null;
    }
}
