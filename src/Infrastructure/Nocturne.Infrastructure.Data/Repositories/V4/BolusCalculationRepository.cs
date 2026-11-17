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
/// Repository for managing bolus calculation records in the database. A deduplication
/// participant, so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (dedup <c>BulkCreateAsync</c>, audited soft-deletes).
/// </summary>
public class BolusCalculationRepository : V4RepositoryBase<BolusCalculation, BolusCalculationEntity>, IBolusCalculationRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BolusCalculationRepository"/> class.
    /// </summary>
    public BolusCalculationRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<BolusCalculationRepository> logger,
        IV4RecordBroadcaster<BolusCalculation>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null
    )
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override BolusCalculationEntity ToEntity(BolusCalculation model) => BolusCalculationMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BolusCalculation ToDomain(BolusCalculationEntity entity) => BolusCalculationMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusCalculationEntity target, BolusCalculation source) => BolusCalculationMapper.UpdateEntity(target, source);
}
