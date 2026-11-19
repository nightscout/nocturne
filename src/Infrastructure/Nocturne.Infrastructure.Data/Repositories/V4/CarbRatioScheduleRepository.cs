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
/// Repository for managing carbohydrate ratio schedules in the database. A LegacyId-only type (no
/// SyncId-upsert, no deduplication), so it uses the shared
/// <see cref="ProfileScopedRepositoryBase{TModel,TEntity}"/> behaviour unchanged.
/// </summary>
public class CarbRatioScheduleRepository : ProfileScopedRepositoryBase<CarbRatioSchedule, CarbRatioScheduleEntity>, ICarbRatioScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CarbRatioScheduleRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public CarbRatioScheduleRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<CarbRatioScheduleRepository> logger, IV4RecordBroadcaster<CarbRatioSchedule>? broadcaster = null, ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads)
    {
    }

    /// <inheritdoc />
    protected override CarbRatioScheduleEntity ToEntity(CarbRatioSchedule model) => CarbRatioScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override CarbRatioSchedule ToDomain(CarbRatioScheduleEntity entity) => CarbRatioScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CarbRatioScheduleEntity target, CarbRatioSchedule source) =>
        CarbRatioScheduleMapper.UpdateEntity(target, source);
}
