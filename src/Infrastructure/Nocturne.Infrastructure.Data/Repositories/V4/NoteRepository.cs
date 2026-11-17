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
/// Repository for managing note records in the database. A deduplication participant on top of
/// the sync-key upsert of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it adds nothing to
/// the base reads.
/// </summary>
public class NoteRepository : SyncUpsertRepositoryBase<Note, NoteEntity>, INoteRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoteRepository"/> class.
    /// </summary>
    public NoteRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<NoteRepository> logger,
        IV4RecordBroadcaster<Note>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override NoteEntity ToEntity(Note model) => NoteMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Note ToDomain(NoteEntity entity) => NoteMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(NoteEntity target, Note source) => NoteMapper.UpdateEntity(target, source);
}
