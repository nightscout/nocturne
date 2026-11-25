using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// <see cref="SyncKeyedRepositoryBase{TModel,TEntity}"/> for the record types whose creates upsert on
/// the sync key: a create — single or bulk — whose (DataSource, SyncIdentifier) matches a stored row
/// for the tenant updates that row in place instead of inserting a duplicate, so a connector replaying
/// its catch-up window is idempotent and a re-corrected value moves the stored record rather than
/// doubling it. Tenant scoping is implicit via the DbContext's RLS-equivalent query filter.
/// </summary>
/// <typeparam name="TModel">The V4 domain record type.</typeparam>
/// <typeparam name="TEntity">The EF entity type backing <typeparamref name="TModel"/>.</typeparam>
public abstract class SyncUpsertRepositoryBase<TModel, TEntity> : SyncKeyedRepositoryBase<TModel, TEntity>
    where TModel : class, IV4Record
    where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISystemTimestamped, ISyncDedupable
{
    /// <inheritdoc />
    protected SyncUpsertRepositoryBase(
        INocturneDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger logger,
        IRecordChangePublisher? changes = null,
        IDataEventSink<Entry>? entrySink = null,
        IDuplicateLinker? linker = null)
        : base(contextFactory, auditContext, logger, changes, entrySink, linker)
    {
    }

    /// <summary>
    /// Identity for this type is the sync key, which a legacy-id match neither honours nor
    /// deduplicates through; the batch path is <see cref="V4RepositoryBase{TModel,TEntity}.BulkCreateAsync"/>.
    /// </summary>
    public override Task<LegacyUpsertBatch<TModel>> BulkUpsertByLegacyIdAsync(
        IReadOnlyList<TModel> records,
        WriteOrigin origin,
        bool preserveStoredCorrelationId = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{typeof(TModel).Name} upserts on its sync key; use BulkCreateAsync for a batch.");

    /// <summary>
    /// Creates a record, or updates in place the stored row carrying the same
    /// (DataSource, SyncIdentifier).
    /// </summary>
    /// <remarks>
    /// Sealed so the single write has one override point, <see cref="UpsertAsync"/>, that both entry
    /// points pass through.
    /// </remarks>
    /// <param name="model">The record to create.</param>
    /// <param name="origin">Whether the write is live or a backfill import.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated record.</returns>
    /// <exception cref="RecreationBlockedException">
    /// The key is held by a row the user deleted, per
    /// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/> — the same rule
    /// <see cref="SplitUpsertsAsync"/> applies to a batch.
    /// </exception>
    public sealed override async Task<TModel> CreateAsync(TModel model, WriteOrigin origin, CancellationToken ct = default)
        => (await UpsertAsync(model, origin, ct)).Record;

    /// <inheritdoc cref="ISyncKeyedRepository{T}.UpsertAsync" />
    public virtual async Task<SyncUpsert<TModel>> UpsertAsync(TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        using var live = LiveWriteScope.Open(origin == WriteOrigin.Live);
        await using var ctx = await ContextFactory.CreateAsync(ct);
        // The domain models carry no sync-key contract, so the key is read off the mapped entity.
        var entity = ToEntity(model);
        var dataSource = entity.DataSource;
        var syncIdentifier = entity.SyncIdentifier;
        if (!string.IsNullOrEmpty(dataSource) && !string.IsNullOrEmpty(syncIdentifier))
        {
            var existing = await ctx.WriteWithHistoryClampLiftedAsync(
                token => FindGoverningRowAsync(ctx, dataSource, syncIdentifier, token), ct);

            if (existing?.DeletedAt != null)
                throw RecreationBlockedException.ForSyncKey(typeof(TModel).Name, dataSource, syncIdentifier);

            if (existing != null)
            {
                var hidden = (await HiddenAsync(ctx, [existing], ct)).Count > 0;
                var before = ToDomain(existing);
                WriteSyncUpsert(existing, model, hidden);
                if (hidden)
                    await ctx.WriteWithHistoryClampLiftedAsync(
                        token => SaveWriteAsync(ctx, existing, before.Timestamp, token), ct);
                else
                    await SaveWriteAsync(ctx, existing, before.Timestamp, ct);
                var upserted = hidden ? AsSent(entity, existing) : ToDomain(existing);
                // A single explicit upsert always broadcasts (no material-change gate on the single path).
                await RaiseBroadcastAsync([], [upserted], [], origin, ct, vacated: [before]);
                return new SyncUpsert<TModel>(upserted, Created: false);
            }
        }

        return new SyncUpsert<TModel>(await InsertAsync(ctx, entity, origin, ct), Created: true);
    }

    /// <summary>
    /// Writes a create onto the stored row its sync key matched, on both the single and the batch
    /// path. The caller never read that row, so a type holding server-resolved state the write
    /// cannot express overrides this to keep it; the default is <see cref="V4RepositoryBase{TModel,TEntity}.ApplyUpdate"/>.
    /// </summary>
    protected virtual void ApplySyncUpsert(TEntity existing, TModel model) => ApplyUpdate(existing, model);

    /// <summary>
    /// <see cref="ApplySyncUpsert"/>, keeping the row's stored legacy id over the one the create
    /// carries. That id is the key the row was first answered and written back under; a resend
    /// that names the row only by its sync key, under another id, would otherwise move it, and
    /// write-back would look for the row's copy upstream under a key that copy never had. A row the
    /// history clamp hides also keeps its correlation id (<see cref="V4RepositoryBase{TModel,TEntity}.KeepCorrelationId"/>).
    /// </summary>
    private void WriteSyncUpsert(TEntity existing, TModel model, bool hidden)
    {
        var storedLegacyId = existing.LegacyId;
        var storedCorrelationId = existing.CorrelationId;
        ApplySyncUpsert(existing, model);
        if (!string.IsNullOrEmpty(storedLegacyId))
            existing.LegacyId = storedLegacyId;
        if (hidden)
            KeepCorrelationId(existing, storedCorrelationId);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.ISyncKeyedRepository{T}.IsRecreationBlockedAsync" />
    /// <remarks>
    /// Reads with the history clamp lifted, as <see cref="UpsertAsync"/> matches, so it refuses what that
    /// write would refuse. It tells the caller no more than the write's own refusal does.
    /// </remarks>
    public async Task<bool> IsRecreationBlockedAsync(
        string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.WriteWithHistoryClampLiftedAsync(
            token => FindGoverningRowAsync(ctx, dataSource, syncIdentifier, token), ct) is { DeletedAt: not null };
    }

    /// <inheritdoc cref="ISyncKeyedRepository{T}.GetUpsertTargetTimestampsAsync" />
    /// <remarks>
    /// Reads with the history clamp lifted, as the write matches, so the footprint holds the stored time of
    /// a row the clamp hides. The times only key cache clears and never reach the caller.
    /// </remarks>
    public async Task<IReadOnlyList<DateTime>> GetUpsertTargetTimestampsAsync(
        IReadOnlyCollection<TModel> records, CancellationToken ct = default)
    {
        var keys = records
            .Select(ToEntity)
            .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
            .Select(e => (DataSource: e.DataSource!, SyncIdentifier: e.SyncIdentifier!))
            .ToHashSet();
        if (keys.Count == 0)
            return [];

        var sources = keys.Select(k => k.DataSource).Distinct().ToList();
        var syncIds = keys.Select(k => k.SyncIdentifier).Distinct().ToList();

        await using var ctx = await ContextFactory.CreateAsync(ct);
        // Over-fetches by a Cartesian amount, as SplitUpsertsAsync does, on the same index.
        var rows = await ctx.WriteWithHistoryClampLiftedAsync(
            token => ctx.Set<TEntity>().AsNoTracking()
                .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
                .Select(e => new { e.DataSource, e.SyncIdentifier, e.Timestamp })
                .ToListAsync(token),
            ct);

        return [.. rows.Where(r => keys.Contains((r.DataSource!, r.SyncIdentifier!))).Select(r => r.Timestamp)];
    }

    /// <summary>
    /// The row that decides a write on the sync key, tracked so a matched live row can be updated
    /// in place. Reads past the soft-delete query filter, which would hide the tombstone holding
    /// the key.
    /// </summary>
    /// <seealso cref="SoftDeleteDedupExtensions.GoverningRow{TEntity}"/>
    private static async Task<TEntity?> FindGoverningRowAsync(
        NocturneDbContext ctx, string dataSource, string syncIdentifier, CancellationToken ct)
        => (await ctx.Set<TEntity>().IncludingDeleted()
                .WhereBlocksRecreation()
                .Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
                .ToListAsync(ct))
            .GoverningRow();

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and apply the write to them. Persists the updates inside the
    /// transaction before returning so the base's insert loop (which clears the tracker) doesn't lose them.
    /// A key held by a row the user deleted drops the record from the batch, per
    /// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>, and is counted in
    /// <see cref="V4RepositoryBase{TModel,TEntity}.UpsertSplit.SkippedDeleted"/>.
    /// </summary>
    protected override async Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<TEntity> entities, CancellationToken ct)
    {
        // Records without both keys keep a unique grouping key so they're not collapsed.
        entities = entities
            .GroupBy(e => e.SyncKey() is { } key ? $"sync|{key}" : $"id|{e.Id}")
            .Select(g => g.Last())
            .ToList();

        if (!entities.Any(e => e.SyncKey() is not null))
            return UpsertSplit.Empty(entities);

        var existingByKey = await ctx.WriteWithHistoryClampLiftedAsync(
            token => ctx.Set<TEntity>().IncludingDeleted().GoverningRowsBySyncKeyAsync(entities, token), ct);
        var hidden = await HiddenAsync(ctx, existingByKey.Values, ct);

        var toInsert = new List<TEntity>();
        var split = UpsertSplit.Empty(toInsert);
        var skippedDeleted = 0;
        foreach (var entity in entities)
        {
            if (entity.SyncKey() is { } key && existingByKey.TryGetValue(key, out var existing))
            {
                if (existing.DeletedAt != null)
                {
                    skippedDeleted++;
                    continue;
                }

                var before = ToDomain(existing);
                var isHidden = hidden.Contains(existing);
                WriteSyncUpsert(existing, ToDomain(entity), isHidden);
                split.Classify(ctx, existing, before, isHidden ? entity : null);
            }
            else
            {
                toInsert.Add(entity);
            }
        }

        await split.SaveAsync(ctx, ct);
        return split with { SkippedDeleted = skippedDeleted };
    }
}
