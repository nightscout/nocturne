using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing carbohydrate intake records in the database. A deduplication
/// participant on top of the sync-key upsert and keyed delete of
/// <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only the <c>GetAsync</c>
/// overload that adds a keyset cursor to the base read.
/// </summary>
public class CarbIntakeRepository : SyncUpsertRepositoryBase<CarbIntake, CarbIntakeEntity>, ICarbIntakeRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CarbIntakeRepository"/> class.
    /// </summary>
    public CarbIntakeRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<CarbIntakeRepository> logger,
        IV4RecordBroadcaster<CarbIntake>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override CarbIntakeEntity ToEntity(CarbIntake model) => CarbIntakeMapper.ToEntity(model);

    /// <inheritdoc />
    protected override CarbIntake ToDomain(CarbIntakeEntity entity) => CarbIntakeMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CarbIntakeEntity target, CarbIntake source) => CarbIntakeMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets carbohydrate intake records based on filter criteria.
    /// Hides the non-primary copies of duplicate groups.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate intakes.</returns>
    public async Task<IEnumerable<CarbIntake>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await PagedRead(
            ctx.CarbIntakes.AsNoTracking(), from, to, device, source, limit, offset, descending,
            afterTimestamp: afterTimestamp, afterId: afterId).ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel);
    }

    public async Task<IReadOnlyList<CarbIntake>> GetMealCandidatesAsync(
        DateTime from,
        DateTime to,
        bool demo,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx
            .CarbIntakes.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to && e.Carbs > 0);

        query = demo
            ? query.Where(e => e.DataSource == DataSources.DemoService)
            : query.Where(e => e.DataSource != DataSources.DemoService);

        var entities = await query.OrderByDescending(e => e.Timestamp).ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel).ToList();
    }
}
