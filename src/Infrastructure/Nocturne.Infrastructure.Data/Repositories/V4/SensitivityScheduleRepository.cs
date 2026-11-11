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
/// Repository for managing insulin sensitivity schedules (ISF) in the database. A LegacyId-only type
/// (no SyncId-upsert, no deduplication), so it uses the shared
/// <see cref="ProfileScopedRepositoryBase{TModel,TEntity}"/> behaviour unchanged.
/// </summary>
public class SensitivityScheduleRepository : ProfileScopedRepositoryBase<SensitivitySchedule, SensitivityScheduleEntity>, ISensitivityScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SensitivityScheduleRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public SensitivityScheduleRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<SensitivityScheduleRepository> logger, IV4RecordBroadcaster<SensitivitySchedule>? broadcaster = null)
        : base(contextFactory, auditContext, logger, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override SensitivityScheduleEntity ToEntity(SensitivitySchedule model) => SensitivityScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override SensitivitySchedule ToDomain(SensitivityScheduleEntity entity) => SensitivityScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(SensitivityScheduleEntity target, SensitivitySchedule source) =>
        SensitivityScheduleMapper.UpdateEntity(target, source);
}
