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
/// Repository for temporary basal records: a sync-key upsert participant and a DeduplicationService
/// participant, keeping only the span-specific queries, the non-primary read filter, the post-commit
/// dedup linking and the field preservation of <see cref="ApplySyncUpsert"/>.
/// </summary>
public class TempBasalRepository : SyncUpsertRepositoryBase<TempBasal, TempBasalEntity>, ITempBasalRepository
{
    private readonly IDeduplicationService _deduplicationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TempBasalRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional native V4 broadcaster; null disables broadcasting.</param>
    public TempBasalRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<TempBasalRepository> logger,
        IV4RecordBroadcaster<TempBasal>? broadcaster = null)
        : base(contextFactory, auditContext, logger, broadcaster)
    {
        _deduplicationService = deduplicationService;
    }

    /// <inheritdoc />
    protected override TempBasalEntity ToEntity(TempBasal model) => TempBasalMapper.ToEntity(model);

    /// <inheritdoc />
    protected override TempBasal ToDomain(TempBasalEntity entity) => TempBasalMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(TempBasalEntity target, TempBasal source) => TempBasalMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    protected internal override RecordType? DedupRecordType => RecordType.TempBasal;

    /// <summary>
    /// The base query with the non-primary LinkedRecords exclusion, which
    /// <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/> applies too.
    /// </summary>
    public override async Task<IEnumerable<TempBasal>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.TempBasals.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);

        query = ApplyReadVisibility(query, ctx);

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(TempBasalMapper.ToDomainModel);
    }

    /// <summary>
    /// Links committed inserts into canonical groups, best-effort: a failure is logged rather than
    /// failing a committed write. Only the full dedup job links a row missed here; the reconcile
    /// pass reads links, so it never sees one.
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<TempBasalEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? DeduplicationInput.UnknownDataSource,
                Criteria: MatchCriteriaMapper.From(e)
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.TempBasal, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "TempBasal", inserted.Count);
        }
    }

    /// <summary>
    /// Keeps the stored value of every field the write path cannot express.
    /// </summary>
    /// <remarks>
    /// The match is made from the incoming record alone — the caller never read the stored row, so a
    /// retry carries no value for server-resolved links (device attribution, resolved insulin context)
    /// or for identity carried in from an import. Taking the incoming null for those would drop state
    /// the retry never disputed: a re-upload after a device's usage window moved, or after the device
    /// was removed, would unattribute a row that back-stamping had already resolved. Fields the request
    /// can carry stay unconditional, so an omitted one still means "clear it".
    /// </remarks>
    protected override void ApplySyncUpsert(TempBasalEntity entity, TempBasal model)
    {
        var deviceId = entity.DeviceId;
        var patientDeviceId = entity.PatientDeviceId;
        var legacyId = entity.LegacyId;
        var insulinContextJson = entity.InsulinContextJson;
        var additionalPropertiesJson = entity.AdditionalPropertiesJson;

        TempBasalMapper.UpdateEntity(entity, model);

        entity.DeviceId ??= deviceId;
        entity.PatientDeviceId ??= patientDeviceId;
        entity.LegacyId ??= legacyId;
        entity.InsulinContextJson ??= insulinContextJson;
        entity.AdditionalPropertiesJson ??= additionalPropertiesJson;
    }

    /// <inheritdoc />
    public async Task<int> SoftDeleteAbsentBySourceAndDateRangeAsync(
        string source,
        DateTime from,
        DateTime to,
        IReadOnlySet<string> keepLegacyIds,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        // The global query filter already restricts to active (DeletedAt == null) rows for this
        // tenant. Soft-delete only the window's rows whose legacy id the source no longer reports;
        // a row with no legacy id can't be matched against the incoming set, so treat it as absent.
        return await ctx.AuditedSoftDeleteAsync(
            AbsentFromSource(ctx, source, from, to, keepLegacyIds),
            AuditContext, $"data_source={source}", ct);
    }

    /// <summary>
    /// The rows <see cref="SoftDeleteAbsentBySourceAndDateRangeAsync"/> removes. The set is bound as
    /// an array for the reason given on <see cref="DeduplicationService.PrimariesOf"/>.
    /// </summary>
    internal static IQueryable<TempBasalEntity> AbsentFromSource(
        NocturneDbContext ctx, string source, DateTime from, DateTime to, IReadOnlySet<string> keepLegacyIds)
    {
        var keep = keepLegacyIds.ToArray();
        return ctx.TempBasals.Where(e => e.DataSource == source
            && e.Timestamp >= from && e.Timestamp <= to
            && (e.LegacyId == null || !keep.Contains(e.LegacyId)));
    }

    /// <inheritdoc />
    public async Task<TempBasal?> GetActiveAtAsync(DateTime at, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.TempBasals
            .AsNoTracking()
            .Where(t => t.Timestamp <= at && (t.EndTimestamp == null || t.EndTimestamp > at))
            .ExcludeNonPrimary(ctx, RecordType.TempBasal)
            .OrderByDescending(t => t.Timestamp)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : TempBasalMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    /// <remarks>Windows on the span start, the timestamp temp basals are attributed by.</remarks>
    public async Task<IReadOnlyList<TempBasal>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<TempBasalEntity>(from, to, limit, ct);
        return entities.Select(TempBasalMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<TempBasalEntity>(patientDeviceIdByRecordId, ct);
    }
}
