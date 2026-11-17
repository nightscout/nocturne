using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// Repository for managing bolus records. A deduplication participant on top of the sync-key
/// upsert and keyed delete of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only
/// the <c>GetAsync</c> overload that adds a bolus kind and a keyset cursor to the base read.
/// </summary>
public class BolusRepository : SyncUpsertRepositoryBase<Bolus, BolusEntity>, IBolusRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BolusRepository"/> class.
    /// </summary>
    public BolusRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<BolusRepository> logger,
        IV4RecordBroadcaster<Bolus>? broadcaster = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
    }

    /// <inheritdoc />
    protected override BolusEntity ToEntity(Bolus model) => BolusMapper.ToEntity(model);

    /// <inheritdoc />
    protected override Bolus ToDomain(BolusEntity entity) => BolusMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BolusEntity target, Bolus source) => BolusMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets bolus records based on filter criteria.
    /// Hides the non-primary copies of duplicate groups.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="kind">Optional bolus kind filter.</param>
    /// <param name="afterTimestamp">Keyset cursor timestamp. When paired with <paramref name="afterId"/>, replaces offset-based pagination.</param>
    /// <param name="afterId">Keyset cursor record ID (tiebreaker). When paired with <paramref name="afterTimestamp"/>, replaces offset-based pagination.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of bolus records.</returns>
    public async Task<IEnumerable<Bolus>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        BolusKind? kind = null,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await PagedRead(
            ctx.Boluses.AsNoTracking(), from, to, device, source, limit, offset, descending,
            query => kind.HasValue ? query.Where(e => e.BolusKind == kind.Value.ToString()) : query,
            afterTimestamp, afterId).ToListAsync(ct);
        return entities.Select(BolusMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bolus>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<BolusEntity>(from, to, limit, ct);
        return entities.Select(BolusMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<BolusEntity>(patientDeviceIdByRecordId, ct);
    }

    public async Task<Guid?> GetCorrelationIdBySyncKeyAsync(
        string dataSource,
        string syncIdentifier,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx
            .Boluses.AsNoTracking()
            .Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
            .Select(e => e.CorrelationId)
            .FirstOrDefaultAsync(ct);
    }
}
