using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for <see cref="BasalInjection"/> records (discrete long-acting basal insulin
/// injections, MDI). SyncId-upsert keyed; never cross-connector dedup-linked.
/// </summary>
public class BasalInjectionRepository : V4RepositoryBase<BasalInjection, BasalInjectionEntity>, IBasalInjectionRepository
{
    /// <inheritdoc />
    public BasalInjectionRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        IV4RecordBroadcaster<BasalInjection>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override BasalInjectionEntity ToEntity(BasalInjection model) => BasalInjectionMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BasalInjection ToDomain(BasalInjectionEntity entity) => BasalInjectionMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BasalInjectionEntity target, BasalInjection source) => BasalInjectionMapper.UpdateEntity(target, source);

    /// <summary>
    /// Creates a new basal injection record. When <c>DataSource</c> and <c>SyncIdentifier</c>
    /// match an existing row for this tenant, the record is updated in place (upsert) rather
    /// than inserted — making the operation idempotent for connector replays.
    /// </summary>
    /// <remarks>
    /// The controller layer has its own idempotency check that returns the existing record
    /// unchanged (HTTP semantics). This repository-level upsert exists for non-HTTP callers
    /// (connectors, background services) that need "latest wins" semantics on replay.
    /// </remarks>
    public override async Task<BasalInjection> CreateAsync(BasalInjection model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await ctx.BasalInjections
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                BasalInjectionMapper.UpdateEntity(existing, model);
                await ctx.SaveChangesAsync(ct);
                var upserted = BasalInjectionMapper.ToDomainModel(existing);
                await RaiseBroadcastAsync([], [upserted], [], origin, ct);
                return upserted;
            }
        }

        var entity = BasalInjectionMapper.ToEntity(model);
        ctx.BasalInjections.Add(entity);
        await ctx.SaveChangesAsync(ct);
        var created = BasalInjectionMapper.ToDomainModel(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
    }

    /// <summary>
    /// SyncId-upsert split: intra-batch keep-last per (DataSource, SyncIdentifier), then match existing
    /// rows in the DB by that key and update them in place. Persists the updates inside the transaction
    /// before returning so the base's insert loop (which clears the tracker) doesn't lose them.
    /// </summary>
    protected override async Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<BasalInjectionEntity> entities, CancellationToken ct)
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

        var updatedEntities = new List<BasalInjectionEntity>();
        var materiallyChanged = new List<BasalInjectionEntity>();
        if (syncKeyed.Count == 0)
            return new UpsertSplit(updatedEntities, materiallyChanged, entities);

        var sources = syncKeyed.Select(e => e.DataSource!).Distinct().ToList();
        var syncIds = syncKeyed.Select(e => e.SyncIdentifier!).Distinct().ToList();

        var existingRows = await ctx.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId)
            .Where(e => sources.Contains(e.DataSource!) && syncIds.Contains(e.SyncIdentifier!))
            .ToListAsync(ct);

        var existingByKey = existingRows
            .GroupBy(e => $"{e.DataSource}|{e.SyncIdentifier}")
            .ToDictionary(g => g.Key, g => g.First());

        var toInsert = new List<BasalInjectionEntity>();
        foreach (var entity in entities)
        {
            var hasKey = !string.IsNullOrEmpty(entity.DataSource)
                && !string.IsNullOrEmpty(entity.SyncIdentifier);
            if (hasKey && existingByKey.TryGetValue($"{entity.DataSource}|{entity.SyncIdentifier}", out var existing))
            {
                // Update in place — mirror the single-record CreateAsync path via the mapper.
                BasalInjectionMapper.UpdateEntity(existing, BasalInjectionMapper.ToDomainModel(entity));
                updatedEntities.Add(existing);
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

    /// <summary>
    /// Soft-deletes every live basal injection matching the given (data source, sync identifier)
    /// pair. The global query filter scopes the lookup to the current tenant and skips rows already
    /// soft-deleted, so a repeat call for the same key returns 0.
    /// </summary>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx,
            ctx.BasalInjections.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            $"sync_identifier={dataSource}/{syncIdentifier}", origin, ct);
    }

    /// <summary>
    /// Finds a single basal injection by data source and sync identifier. The global query
    /// filter automatically scopes the lookup to the current tenant and excludes soft-deleted rows.
    /// </summary>
    public async Task<BasalInjection?> FindBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.BasalInjections
            .FirstOrDefaultAsync(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);
        return entity is null ? null : BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BasalInjection>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<BasalInjectionEntity>(from, to, limit, ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<BasalInjectionEntity>(patientDeviceIdByRecordId, ct);
    }
}
