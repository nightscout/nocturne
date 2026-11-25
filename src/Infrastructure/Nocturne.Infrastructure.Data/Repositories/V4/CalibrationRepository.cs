using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing calibration records. A LegacyId-only type (no SyncId-upsert, no
/// deduplication), so it uses the shared <see cref="V4RepositoryBase{TModel,TEntity}"/>
/// behaviour unchanged plus the calibration-specific queries below.
/// </summary>
public class CalibrationRepository : V4RepositoryBase<Calibration, CalibrationEntity>, ICalibrationRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public CalibrationRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<CalibrationRepository> logger, IRecordChangePublisher? changes = null, IDataEventSink<Entry>? entrySink = null)
        : base(contextFactory, auditContext, logger, changes, entrySink)
    {
    }

    /// <inheritdoc />
    protected override Entry? ProjectToLegacyEntry(Calibration model) => EntryProjection.FromCalibration(model);

    /// <inheritdoc />
    protected override CalibrationEntity ToEntity(Calibration model) => CalibrationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Calibration ToDomain(CalibrationEntity entity) => CalibrationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CalibrationEntity target, Calibration source) =>
        CalibrationMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<int> DeleteAsync(RecordSelector selector, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, ctx.Calibrations.Selected(selector), selector.Scope, DuplicateDelete.PromoteSurvivor, origin, ct);
    }

    /// <summary>
    /// Deletes all calibration records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or null for no lower bound.</param>
    /// <param name="to">Inclusive end, or null for no upper bound.</param>
    /// <param name="origin">Whether the delete is a live event; see <see cref="V4RepositoryBase{TModel,TEntity}.RaiseBroadcastAsync"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    public async Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Calibrations.InWindow(from, to);

        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, query, $"timestamp={from:O}..{to:O}", DuplicateDelete.PromoteSurvivor, origin, ct);
    }
}
