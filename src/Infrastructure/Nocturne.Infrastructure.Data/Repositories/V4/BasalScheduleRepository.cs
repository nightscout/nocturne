using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing basal schedules in the database. A LegacyId-only type (no SyncId-upsert,
/// no deduplication), so it uses the shared
/// <see cref="ProfileScopedRepositoryBase{TModel,TEntity}"/> behaviour unchanged.
/// </summary>
public class BasalScheduleRepository : ProfileScopedRepositoryBase<BasalSchedule, BasalScheduleEntity>, IBasalScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BasalScheduleRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public BasalScheduleRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<BasalScheduleRepository> logger, IV4RecordBroadcaster<BasalSchedule>? broadcaster = null)
        : base(contextFactory, auditContext, logger, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override BasalScheduleEntity ToEntity(BasalSchedule model) => BasalScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BasalSchedule ToDomain(BasalScheduleEntity entity) => BasalScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BasalScheduleEntity target, BasalSchedule source) =>
        BasalScheduleMapper.UpdateEntity(target, source);
}
