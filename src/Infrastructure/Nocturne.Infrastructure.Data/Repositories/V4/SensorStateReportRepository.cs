using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for <see cref="SensorStateReport"/> records. Its rows carry no legacy id: each is keyed
/// by its status's correlation id, which <see cref="SplitUpsertsAsync"/> holds the way the extras row's
/// bulk create does.
/// </summary>
public class SensorStateReportRepository
    : V4RepositoryBase<SensorStateReport, SensorStateReportEntity>, ISensorStateReportRepository
{
    private const string Ok = nameof(SensorState.Ok);

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorStateReportRepository"/> class.
    /// </summary>
    public SensorStateReportRepository(
        INocturneDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<SensorStateReportRepository> logger,
        IRecordChangePublisher? changes = null)
        : base(contextFactory, auditContext, logger, changes)
    {
    }

    /// <inheritdoc />
    protected override SensorStateReportEntity ToEntity(SensorStateReport model) => SensorStateReportMapper.ToEntity(model);

    /// <inheritdoc />
    protected override SensorStateReport ToDomain(SensorStateReportEntity entity) => SensorStateReportMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(SensorStateReportEntity target, SensorStateReport source) =>
        SensorStateReportMapper.UpdateEntity(target, source);

    /// <summary>
    /// Keeps the first row of each correlation id in the batch, and drops one a stored row holds, live
    /// or deleted by the user (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>). The
    /// check reads with the history clamp lifted, so a stored row the clamp hides holds its id as a shown one
    /// does; it only decides what to insert, and returns no row.
    /// </summary>
    protected override async Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<SensorStateReportEntity> entities, CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        var first = entities.Where(e => e.CorrelationId is not { } id || seen.Add(id)).ToList();
        HashSet<Guid> correlationIds = [.. first.Select(e => e.CorrelationId).OfType<Guid>()];
        var blocked = await ctx.WriteWithHistoryClampLiftedAsync(
            token => ctx.GetBlockingCorrelationIdsAsync<SensorStateReportEntity>(correlationIds, token), ct);
        return UpsertSplit.Empty(
            [.. first.Where(e => e.CorrelationId is not { } id || !blocked.Held.Contains(id))],
            first.Count(e => e.CorrelationId is { } id && blocked.DeletedByUser.Contains(id)));
    }

    /// <inheritdoc />
    public async Task<ArrivedSensorStateReport?> GetNewestAsync(
        string device, string dataSource, DateTime since, DateTime asOf, DateTime latestTimestamp,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var newest = await ctx.SensorStateReports
            .AsNoTracking()
            .Where(e => e.DataSource == dataSource && e.Device == device
                        && e.Timestamp >= since && e.Timestamp <= latestTimestamp
                        && e.SysCreatedAt >= since && e.SysCreatedAt <= asOf)
            .Select(e => new { Entity = e, ArrivedAt = e.Timestamp < e.SysCreatedAt ? e.Timestamp : e.SysCreatedAt })
            .OrderByDescending(r => r.ArrivedAt)
            .ThenBy(r => r.Entity.State == null ? 1 : r.Entity.State == Ok ? 2 : 0)
            .ThenBy(r => r.Entity.Id)
            .FirstOrDefaultAsync(ct);
        return newest is null
            ? null
            : new ArrivedSensorStateReport(SensorStateReportMapper.ToDomainModel(newest.Entity), newest.ArrivedAt);
    }

    /// <inheritdoc />
    public async Task<int> DeleteByCorrelationIdAsync(Guid correlationId, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, ctx.SensorStateReports.Where(e => e.CorrelationId == correlationId), $"correlation_id={correlationId}",
            DuplicateDelete.EveryCopy, origin, ct);
    }
}
