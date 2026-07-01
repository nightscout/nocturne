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
/// Shared CRUD, soft-delete, and LegacyId-deduplicated bulk-create implementation for the V4 record
/// repositories. Concrete repositories supply three static-mapper bridges
/// (<see cref="ToEntity"/>, <see cref="ToDomain"/>, <see cref="ApplyUpdate"/>) and keep only their
/// type-specific query methods; everything that was previously copy-pasted across every repository
/// lives here once.
/// </summary>
/// <remarks>
/// Behaviour is intentionally identical to the pre-refactor per-type repositories (the golden suite
/// holds it constant). Per-type strategy points that some types diverge on — SyncId-upsert,
/// DeduplicationService participation, non-primary-excluding counts — are exposed as
/// <see langword="virtual"/> members (<see cref="BulkCreateAsync"/>, <see cref="CountAsync"/>) for
/// the dedup participants to override; the base implements the LegacyId-only path.
/// </remarks>
/// <typeparam name="TModel">The V4 domain record type.</typeparam>
/// <typeparam name="TEntity">The EF entity type backing <typeparamref name="TModel"/>.</typeparam>
public abstract class V4RepositoryBase<TModel, TEntity>
    where TModel : class, IV4Record
    where TEntity : class, IV4TimeSeriesEntity, IAuditable
{
    /// <summary>Tenant-scoped context factory. Exposed so subclasses can implement type-specific queries.</summary>
    protected ITenantDbContextFactory ContextFactory { get; }

    /// <summary>
    /// Actor/request metadata for the audited soft-delete path. Exposed so subclasses' type-specific
    /// audited deletes (e.g. DeleteBySyncIdentifierAsync, DeleteByLegacyIdPrefixAsync) share the same
    /// attribution as the base DeleteByLegacyIdAsync.
    /// </summary>
    protected IAuditContext AuditContext { get; }

    /// <summary>
    /// Broadcasts native V4 record shapes to the chokepoint's realtime category. Optional: when null
    /// (e.g. a repo constructed without DI) writes simply broadcast nothing. Fired for live writes only —
    /// the <see cref="WriteOrigin"/> gate is applied in <see cref="RaiseBroadcastAsync"/>.
    /// </summary>
    private readonly IV4RecordBroadcaster<TModel>? _broadcaster;

    /// <summary>
    /// Legacy <see cref="Entry"/> sink fired for glucose-family writes alongside the native V4 broadcast,
    /// so V1/V3 clients on the legacy <c>entries</c> collection still see realtime updates. Optional: when
    /// null (e.g. a non-glucose type or a repo constructed without DI) no entries projection is fired.
    /// Gated to <see cref="WriteOrigin.Live"/> in <see cref="RaiseEntriesProjectionAsync"/>.
    /// </summary>
    private readonly IDataEventSink<Entry>? _entrySink;

    /// <summary>Initializes the base with the tenant-scoped context factory, audit context, (optional) broadcaster, and (optional) legacy entry sink.</summary>
    protected V4RepositoryBase(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        IV4RecordBroadcaster<TModel>? broadcaster = null,
        IDataEventSink<Entry>? entrySink = null)
    {
        ContextFactory = contextFactory;
        AuditContext = auditContext;
        _broadcaster = broadcaster;
        _entrySink = entrySink;
    }

    /// <summary>
    /// Projects a domain model to the legacy <see cref="Entry"/> shape, or null when the type has no
    /// legacy projection. The glucose-family repos override this; non-glucose types project nothing.
    /// </summary>
    protected virtual Entry? ProjectToLegacyEntry(TModel model) => null;

    /// <summary>
    /// Fires the native V4 broadcast for a just-committed write — but only for <see cref="WriteOrigin.Live"/>
    /// writes (backfill imports stay silent so clients aren't flooded). The single gate every mutating
    /// method routes through, so the origin rule lives in exactly one place.
    /// </summary>
    protected async Task RaiseBroadcastAsync(
        IReadOnlyList<TModel> created,
        IReadOnlyList<TModel> updated,
        IReadOnlyList<TModel> deleted,
        WriteOrigin origin,
        CancellationToken ct)
    {
        await V4RecordBroadcast.RaiseAsync(
            _broadcaster, created, updated, deleted.Select(m => m.Id).ToList(), origin, ct);
        await RaiseEntriesProjectionAsync(created, updated, deleted, origin, ct);
    }

    /// <summary>
    /// Projects glucose-family writes to the legacy <see cref="Entry"/> shape and fires the legacy entry
    /// sink — gated to <see cref="WriteOrigin.Live"/>, and a no-op when no sink is wired or the type has no
    /// projection (<see cref="ProjectToLegacyEntry"/> returns null).
    /// </summary>
    private async Task RaiseEntriesProjectionAsync(
        IReadOnlyList<TModel> created,
        IReadOnlyList<TModel> updated,
        IReadOnlyList<TModel> deleted,
        WriteOrigin origin,
        CancellationToken ct)
    {
        if (origin != WriteOrigin.Live || _entrySink is null)
            return;

        var createdEntries = created.Select(ProjectToLegacyEntry).OfType<Entry>().ToList();
        if (createdEntries.Count > 0)
            await _entrySink.OnCreatedAsync(createdEntries, ct);

        foreach (var m in updated)
            if (ProjectToLegacyEntry(m) is { } e)
                await _entrySink.OnUpdatedAsync(e, ct);

        foreach (var m in deleted)
            if (ProjectToLegacyEntry(m) is { } e)
                await _entrySink.OnDeletedAsync(e, ct);
    }

    /// <summary>
    /// True if an upserted-in-place tracked entity changed materially (worth an <c>update</c> broadcast).
    /// Same predicate the audit interceptor uses, so "broadcast update" ⟺ "audited change".
    /// </summary>
    protected static bool HasMaterialChange(NocturneDbContext ctx, TEntity entity)
        => V4MaterialChange.HasMaterialChange(ctx.Entry(entity));

    // ── Per-type static-mapper bridges (the only code the base needs from each repository) ──

    /// <summary>Maps a domain model to its EF entity (delegates to the type's static mapper).</summary>
    protected abstract TEntity ToEntity(TModel model);

    /// <summary>Maps an EF entity to its domain model (delegates to the type's static mapper).</summary>
    protected abstract TModel ToDomain(TEntity entity);

    /// <summary>Applies a domain model's values onto an existing tracked entity (in-place update).</summary>
    protected abstract void ApplyUpdate(TEntity target, TModel source);

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.GetAsync" />
    /// <remarks>
    /// Virtual so dedup participants (which expose an extended overload with a non-primary
    /// LinkedRecords filter + keyset cursor) can override this 7-arg form to route through their
    /// filtered overload, preserving the pre-base default-interface bridge behaviour.
    /// </remarks>
    public virtual async Task<IEnumerable<TModel>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Set<TEntity>().AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(ToDomain);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.GetByIdAsync" />
    public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().FindAsync([id], ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <summary>Returns a single record by its legacy (MongoDB ObjectId) identifier, or null.</summary>
    public async Task<TModel?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.CreateAsync" />
    /// <remarks>Virtual: SyncId-upsert types (Bolus, CarbIntake) override to upsert in place.</remarks>
    public virtual async Task<TModel> CreateAsync(TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = ToEntity(model);
        ctx.Set<TEntity>().Add(entity);
        await ctx.SaveChangesAsync(ct);
        var created = ToDomain(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.UpdateAsync" />
    public async Task<TModel> UpdateAsync(Guid id, TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"{typeof(TModel).Name} {id} not found");
        ApplyUpdate(entity, model);
        await ctx.SaveChangesAsync(ct);
        var updated = ToDomain(entity);
        // A single explicit update is always a material change worth broadcasting.
        await RaiseBroadcastAsync([], [updated], [], origin, ct);
        return updated;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.DeleteAsync" />
    public async Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"{typeof(TModel).Name} {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
        var model = ToDomain(entity);
        await RaiseBroadcastAsync([], [], [model], origin, ct);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.RestoreAsync" />
    public async Task<TModel> RestoreAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted {typeof(TModel).Name} {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        // A restored record reappears in the dataset: broadcast it as a create so clients re-add it.
        var restored = ToDomain(entity);
        await RaiseBroadcastAsync([restored], [], [], origin, ct);
        return restored;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.BulkRestoreAsync" />
    public async Task<IEnumerable<TModel>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        var restored = entities.Select(ToDomain).ToList();
        await RaiseBroadcastAsync(restored, [], [], origin, ct);
        return restored;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.GetDeletedAsync" />
    public async Task<IEnumerable<TModel>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(ToDomain);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.CountDeletedAsync" />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Read-visibility hook applied by <see cref="CountAsync"/> (and reusable by future read paths) so
    /// counts match the rows reads return. The base is identity; dedup participants override it to
    /// exclude non-primary LinkedRecords for their RecordType — replacing the per-type CountAsync
    /// overrides that each carried the same exclusion.
    /// </summary>
    protected virtual IQueryable<TEntity> ApplyReadVisibility(IQueryable<TEntity> query, NocturneDbContext ctx) => query;

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.CountAsync" />
    public virtual async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ApplyReadVisibility(ctx.Set<TEntity>().AsNoTracking().AsQueryable(), ctx);
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>Soft-deletes the record(s) with the given legacy id. Returns the number affected.</summary>
    /// <remarks>
    /// Routes through the audited soft-delete helper so every V4 type writes a mutation_audit_log row
    /// and carries the user-delete dedup discriminator. Virtual so types with a type-specific delete
    /// surface can still override.
    /// </remarks>
    public virtual async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.AuditedSoftDeleteWithEntitiesAsync(
            ctx.Set<TEntity>().Where(e => e.LegacyId == legacyId), AuditContext, ct);
        var models = entities.Select(ToDomain).ToList();
        await RaiseBroadcastAsync([], [], models, origin, ct);
        return models.Count;
    }

    /// <summary>Latest stored record timestamp, optionally scoped to a data source (connector watermark).</summary>
    public async Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Set<TEntity>().AsNoTracking().AsQueryable();
        if (source != null) query = query.Where(e => e.DataSource == source);
        return await query.MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>Oldest stored record timestamp, optionally scoped to a data source.</summary>
    public async Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.Set<TEntity>().AsNoTracking().AsQueryable();
        if (source != null) query = query.Where(e => e.DataSource == source);
        return await query.MinAsync(e => (DateTime?)e.Timestamp, ct);
    }

    /// <summary>
    /// The result of <see cref="SplitUpsertsAsync"/>: rows upserted in place (returned to the caller),
    /// the subset of those that changed materially (broadcast as <c>update</c>), and the rows still to
    /// insert. <see cref="MateriallyChanged"/> must be a subset of <see cref="UpdatedInPlace"/>.
    /// </summary>
    protected readonly record struct UpsertSplit(
        List<TEntity> UpdatedInPlace,
        List<TEntity> MateriallyChanged,
        List<TEntity> ToInsert);

    /// <summary>SyncId-upsert types override: match existing rows by (DataSource, SyncIdentifier), update them in
    /// place, and return the upserted rows, those that changed materially, and the rows still to insert.
    /// Default: nothing upserted.</summary>
    protected virtual Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<TEntity> entities, CancellationToken ct)
        => Task.FromResult(new UpsertSplit([], [], entities));

    /// <summary>DeduplicationService participants override: link the just-inserted rows into canonical groups
    /// (runs AFTER commit). Default: no-op.</summary>
    protected virtual Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<TEntity> inserted, WriteOrigin origin, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Bulk-inserts records with batch-level and DB-level deduplication by LegacyId. The base
    /// implements the LegacyId-only path; SyncId-upsert / DeduplicationService participants override
    /// the <see cref="SplitUpsertsAsync"/> / <see cref="PostCommitDedupAsync"/> hooks rather than the
    /// whole method.
    /// </summary>
    public virtual async Task<IEnumerable<TModel>> BulkCreateAsync(
        IEnumerable<TModel> recordsParam, WriteOrigin origin, CancellationToken ct = default)
    {
        var records = recordsParam.ToList();
        if (records.Count == 0) return [];
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            var entities = records.Select(ToEntity).ToList();

            var split = await SplitUpsertsAsync(ctx, entities, ct);
            var toInsert = split.ToInsert;

            // Batch-level LegacyId dedup
            toInsert = toInsert.GroupBy(e => e.LegacyId ?? e.Id.ToString()).Select(g => g.First()).ToList();
            var legacyIds = toInsert.Where(e => !string.IsNullOrEmpty(e.LegacyId)).Select(e => e.LegacyId!).ToHashSet();
            if (legacyIds.Count > 0)
            {
                var blocked = await ctx.GetBlockingLegacyIdsAsync<TEntity>(legacyIds, ct);
                toInsert = toInsert.Where(e => string.IsNullOrEmpty(e.LegacyId) || !blocked.Contains(e.LegacyId)).ToList();
            }

            if (toInsert.Count == 0 && split.UpdatedInPlace.Count == 0) { await tx.CommitAsync(ct); return Enumerable.Empty<TModel>(); }

            const int batchSize = 500;
            foreach (var batch in toInsert.Chunk(batchSize))
            {
                ctx.Set<TEntity>().AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            await PostCommitDedupAsync(ctx, toInsert, origin, ct);
            // Inserts broadcast as create; upserts broadcast as update only when materially changed
            // (a connector re-poll of byte-identical rows changes nothing, so it stays silent).
            await RaiseBroadcastAsync(
                toInsert.Select(ToDomain).ToList(),
                split.MateriallyChanged.Select(ToDomain).ToList(),
                [],
                origin, ct);
            return split.UpdatedInPlace.Concat(toInsert).Select(ToDomain);
        });
    }
}
