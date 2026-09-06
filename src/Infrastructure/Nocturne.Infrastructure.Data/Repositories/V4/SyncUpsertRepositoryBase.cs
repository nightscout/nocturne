using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
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
    where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISyncDedupable
{
    /// <inheritdoc />
    protected SyncUpsertRepositoryBase(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        IV4RecordBroadcaster<TModel>? broadcaster = null,
        IDataEventSink<Entry>? entrySink = null)
        : base(contextFactory, auditContext, broadcaster, entrySink)
    {
    }

    /// <summary>
    /// Creates a record, or updates in place the stored row carrying the same
    /// (DataSource, SyncIdentifier).
    /// </summary>
    /// <param name="model">The record to create.</param>
    /// <param name="origin">Whether the write is live or a backfill import.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated record.</returns>
    public override async Task<TModel> CreateAsync(TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        // The domain models carry no sync-key contract, so the key is read off the mapped entity.
        var entity = ToEntity(model);
        var dataSource = entity.DataSource;
        var syncIdentifier = entity.SyncIdentifier;
        if (!string.IsNullOrEmpty(dataSource) && !string.IsNullOrEmpty(syncIdentifier))
        {
            var existing = await ctx.Set<TEntity>()
                .FirstOrDefaultAsync(
                    e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);
            if (existing != null)
            {
                ApplyUpdate(existing, model);
                await ctx.SaveChangesAsync(ct);
                var upserted = ToDomain(existing);
                // A single explicit upsert always broadcasts (no material-change gate on the single path).
                await RaiseBroadcastAsync([], [upserted], [], origin, ct);
                return upserted;
            }
        }

        ctx.Set<TEntity>().Add(entity);
        await ctx.SaveChangesAsync(ct);
        var created = ToDomain(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
    }

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place. Persists the updates inside the transaction
    /// before returning so the base's insert loop (which clears the tracker) doesn't lose them.
    /// A key held by a row the user deleted drops the record from the batch, per
    /// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>.
    /// </summary>
    protected override async Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<TEntity> entities, CancellationToken ct)
    {
        // Records without both keys keep a unique grouping key so they're not collapsed.
        entities = entities
            .GroupBy(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier)
                ? $"sync|{e.DataSource}|{e.SyncIdentifier}"
                : $"id|{e.Id}")
            .Select(g => g.Last())
            .ToList();

        var syncKeyed = entities
            .Where(e => !string.IsNullOrEmpty(e.DataSource) && !string.IsNullOrEmpty(e.SyncIdentifier))
            .ToList();

        var updatedEntities = new List<TEntity>();
        var materiallyChanged = new List<TEntity>();
        if (syncKeyed.Count == 0)
            return new UpsertSplit(updatedEntities, materiallyChanged, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        // Over-fetches by a Cartesian amount; the partial unique index on
        // (tenant_id, data_source, sync_identifier) keeps this cheap. Tombstones follow
        // WhereBlocksRecreation: a user delete holds the key, a system sweep does not.
        var existingRows = await ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId)
            .WhereBlocksRecreation()
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        // The unique index counts live rows only, so a user tombstone and a live row can share a key.
        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(e => e.DeletedAt == null) ?? g.First());

        var toInsert = new List<TEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                if (existing.DeletedAt != null)
                    continue;

                ApplyUpdate(existing, ToDomain(entity));
                updatedEntities.Add(existing);
                // Capture material changes now, before SaveChanges clears the modified flags.
                if (HasMaterialChange(ctx, existing))
                    materiallyChanged.Add(existing);
            }
            else
            {
                toInsert.Add(entity);
            }
        }

        if (updatedEntities.Count > 0)
        {
            // Persist updates before the insert-chunking loop clears the tracker.
            await ctx.SaveChangesAsync(ct);
        }

        return new UpsertSplit(updatedEntities, materiallyChanged, toInsert);
    }
}
