using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing blood glucose check records in the database. A deduplication
/// participant on top of the sync-key upsert of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>,
/// so it adds nothing to the base reads.
/// </summary>
public class BGCheckRepository : SyncUpsertRepositoryBase<BGCheck, BGCheckEntity>, IBGCheckRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BGCheckRepository"/> class.
    /// </summary>
    public BGCheckRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<BGCheckRepository> logger,
        IV4RecordBroadcaster<BGCheck>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override BGCheckEntity ToEntity(BGCheck model) => BGCheckMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BGCheck ToDomain(BGCheckEntity entity) => BGCheckMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BGCheckEntity target, BGCheck source) => BGCheckMapper.UpdateEntity(target, source);
}
