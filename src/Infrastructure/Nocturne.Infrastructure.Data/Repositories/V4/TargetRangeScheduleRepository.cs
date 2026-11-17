using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing target range schedules in the database. A LegacyId-only type (no
/// SyncId-upsert, no deduplication), so it uses the shared
/// <see cref="ProfileScopedRepositoryBase{TModel,TEntity}"/> behaviour unchanged.
/// </summary>
public class TargetRangeScheduleRepository : ProfileScopedRepositoryBase<TargetRangeSchedule, TargetRangeScheduleEntity>, ITargetRangeScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TargetRangeScheduleRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public TargetRangeScheduleRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<TargetRangeScheduleRepository> logger, IV4RecordBroadcaster<TargetRangeSchedule>? broadcaster = null, ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads)
    {
    }

    /// <inheritdoc />
    protected override TargetRangeScheduleEntity ToEntity(TargetRangeSchedule model) => TargetRangeScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override TargetRangeSchedule ToDomain(TargetRangeScheduleEntity entity) => TargetRangeScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(TargetRangeScheduleEntity target, TargetRangeSchedule source) =>
        TargetRangeScheduleMapper.UpdateEntity(target, source);
}
