using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Logging;
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
    where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISystemTimestamped
{
    /// <summary>Tenant-scoped context factory. Exposed so subclasses can implement type-specific queries.</summary>
    protected ITenantDbContextFactory ContextFactory { get; }

    /// <summary>
    /// Actor/request metadata for the audited soft-delete path. Exposed so subclasses' type-specific
    /// audited deletes (e.g. DeleteBySyncIdentifierAsync, DeleteByLegacyIdPrefixAsync) share the same
    /// attribution as the base DeleteByLegacyIdAsync.
    /// </summary>
    protected IAuditContext AuditContext { get; }

    /// <summary>Carries the bulk paths' report of what they skipped; see <see cref="SkippedWriteLog.LogSkippedDeleted"/>.</summary>
    protected ILogger Logger { get; }

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

    /// <summary>Initializes the base with the tenant-scoped context factory, audit context, logger, (optional) broadcaster, and (optional) legacy entry sink.</summary>
    protected V4RepositoryBase(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger logger,
        IV4RecordBroadcaster<TModel>? broadcaster = null,
        IDataEventSink<Entry>? entrySink = null)
    {
        ContextFactory = contextFactory;
        AuditContext = auditContext;
        Logger = logger;
        _broadcaster = broadcaster;
        _entrySink = entrySink;
    }

    /// <summary>
    /// Projects a domain model to the legacy <see cref="Entry"/> shape, or null when the type has no
    /// legacy projection. The glucose-family repos override this; non-glucose types project nothing.
    /// </summary>
    protected virtual Entry? ProjectToLegacyEntry(TModel model) => null;

    /// <summary>
    /// Filters the models that reach the legacy <see cref="Entry"/> projection. The legacy surface
    /// is single-stream: SensorGlucose overrides this with canonical stream selection so a losing
    /// CGM's live writes don't interleave into v1/v3 clients' dataUpdate feed. Deletes are never
    /// filtered — a previously-broadcast record must always emit its removal. The native V4
    /// broadcast is unaffected (multi-stream access is a V4 concern).
    /// </summary>
    protected virtual Task<IReadOnlyList<TModel>> FilterLegacyProjectionAsync(
        IReadOnlyList<TModel> models, CancellationToken ct) => Task.FromResult(models);

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
        await RaiseLiveCreatedAsync(created, origin, ct);
    }

    /// <summary>
    /// Runs the type's domain reaction to just-created records, sharing the <see cref="WriteOrigin.Live"/>
    /// gate with the broadcast and the legacy projection so backfill imports stay inert here too.
    /// </summary>
    private async Task RaiseLiveCreatedAsync(IReadOnlyList<TModel> created, WriteOrigin origin, CancellationToken ct)
    {
        if (origin != WriteOrigin.Live || created.Count == 0)
            return;

        await OnLiveCreatedAsync(created, ct);
    }

    /// <summary>
    /// Hook for a post-commit domain reaction to live creates. Default is a no-op; only types that own a
    /// reaction override it (device events advance tracker instances). Runs after the write has committed,
    /// so an override that throws faults the caller on work the write itself no longer depends on.
    /// </summary>
    protected virtual Task OnLiveCreatedAsync(IReadOnlyList<TModel> created, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The coarse substitute for per-record delete events when a delete matched more rows than
    /// <see cref="AuditedBulkDeleteExtensions.BroadcastMaterializationCap"/>: the legacy entries sink's
    /// collection-level bulk-delete signal (cache invalidation plus a count-only storage-delete
    /// broadcast). The native V4 port has no coarse form — <see cref="IV4RecordBroadcaster{TModel}"/>
    /// carries record ids only — so V4 subscribers see no delete event and converge on their next read.
    /// </summary>
    private async Task RaiseBulkDeleteBroadcastAsync(int deletedCount, WriteOrigin origin, CancellationToken ct)
    {
        if (origin != WriteOrigin.Live || _entrySink is null || deletedCount <= 0)
            return;

        await _entrySink.OnBulkDeletedAsync(deletedCount, ct);
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

        var visibleCreated = created.Count > 0 ? await FilterLegacyProjectionAsync(created, ct) : created;
        var createdEntries = visibleCreated.Select(ProjectToLegacyEntry).OfType<Entry>().ToList();
        if (createdEntries.Count > 0)
            await _entrySink.OnCreatedAsync(createdEntries, ct);

        var visibleUpdated = updated.Count > 0 ? await FilterLegacyProjectionAsync(updated, ct) : updated;
        foreach (var m in visibleUpdated)
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

    /// <inheritdoc cref="ILegacyKeyedRepository{T}.GetCorrelationIdsByLegacyIdAsync" />
    public async Task<IEnumerable<LegacyCorrelation>> GetCorrelationIdsByLegacyIdAsync(
        IEnumerable<string> legacyIds, CancellationToken ct = default)
    {
        var ids = legacyIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var rows = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.LegacyId != null && ids.Contains(e.LegacyId)
                && e.CorrelationId != null && e.CorrelationId != Guid.Empty)
            .Select(e => new { e.LegacyId, e.CorrelationId })
            .ToListAsync(ct);
        return rows.Select(r => new LegacyCorrelation(r.LegacyId!, r.CorrelationId!.Value)).ToList();
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.GetHeldLegacyIdsAsync" />
    public async Task<IReadOnlySet<string>> GetHeldLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        if (legacyIds.Count == 0)
            return RecreationBlocks<string>.None.Held;

        await using var ctx = await ContextFactory.CreateAsync(ct);
        return (await ctx.GetBlockingLegacyIdsAsync<TEntity>(legacyIds.ToHashSet(StringComparer.Ordinal), ct)).Held;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.GetByGuidRangeAsync" />
    public async Task<TModel?> GetByGuidRangeAsync(Guid low, Guid high, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>()
            .Where(e => e.Id >= low && e.Id <= high)
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.AdoptOwnIdsAsync" />
    public async Task<IEnumerable<TModel>> AdoptOwnIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var ranges = new List<(string Id, Guid Low, Guid High)>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (MongoObjectId.TryGetOwnIdRange(id, out var low, out var high))
                ranges.Add((id, low, high));
        }

        if (ranges.Count == 0)
            return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var claims = new List<(TEntity Row, string LegacyId)>();
        foreach (var chunk in ranges.Chunk(OwnIdRangesPerQuery))
        {
            foreach (var row in await UnkeyedRows(ctx).Where(IdInAnyRange(chunk)).ToListAsync(ct))
                claims.Add((row, chunk.First(r => row.Id >= r.Low && row.Id <= r.High).Id));
        }

        return (await AdoptAsync(ctx, claims, ct)).Select(ToDomain).ToList();
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.AdoptLegacyIdsByCorrelationAsync" />
    public async Task<int> AdoptLegacyIdsByCorrelationAsync(
        IReadOnlyDictionary<Guid, string> legacyIdByCorrelation, CancellationToken ct = default)
    {
        if (legacyIdByCorrelation.Count == 0)
            return 0;

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var correlationIds = legacyIdByCorrelation.Keys.ToList();
        var rows = await UnkeyedRows(ctx)
            .Where(e => e.CorrelationId != null && correlationIds.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);

        var claims = rows.Select(row => (row, legacyIdByCorrelation[row.CorrelationId!.Value])).ToList();
        return (await AdoptAsync(ctx, claims, ct)).Count;
    }

    /// <summary>Bounds the OR chain <see cref="IdInAnyRange"/> builds for one query.</summary>
    private const int OwnIdRangesPerQuery = 200;

    private static IQueryable<TEntity> UnkeyedRows(NocturneDbContext ctx) =>
        ctx.Set<TEntity>()
            .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
            .AsNoTracking()
            .Where(e => e.LegacyId == null);

    private static Expression<Func<TEntity, bool>> IdInAnyRange(IEnumerable<(string Id, Guid Low, Guid High)> ranges)
    {
        var row = Expression.Parameter(typeof(TEntity), "e");
        var id = Expression.Property(row, nameof(IIdentified.Id));
        Expression? any = null;
        foreach (var (_, low, high) in ranges)
        {
            // Closure members rather than constants, so EF sends the bounds as parameters.
            Expression<Func<Guid>> lowBound = () => low;
            Expression<Func<Guid>> highBound = () => high;
            var inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(id, lowBound.Body),
                Expression.LessThanOrEqual(id, highBound.Body));
            any = any is null ? inRange : Expression.OrElse(any, inRange);
        }

        return Expression.Lambda<Func<TEntity, bool>>(any ?? Expression.Constant(false), row);
    }

    /// <summary>
    /// Writes each claimed legacy id onto its row, outside audit and broadcast: the id is the one the
    /// record is already known by on the wire, so what it serves does not change. The unique
    /// legacy-id index counts live rows only, so a live row takes an id only while no live row holds
    /// it, which also lets only one of two live claimants of the same id take it; any number of
    /// deleted rows may share one.
    /// </summary>
    private static async Task<List<TEntity>> AdoptAsync(
        NocturneDbContext ctx, List<(TEntity Row, string LegacyId)> claims, CancellationToken ct)
    {
        var adopted = new List<TEntity>();
        foreach (var (row, legacyId) in claims)
        {
            var id = row.Id;
            var target = ctx.Set<TEntity>()
                .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                .Where(e => e.Id == id && e.LegacyId == null);
            if (row.DeletedAt is null)
                target = target.Where(_ => !ctx.Set<TEntity>().Any(held => held.LegacyId == legacyId));

            if (await target.ExecuteUpdateAsync(s => s.SetProperty(e => e.LegacyId, legacyId), ct) == 0)
                continue;

            row.LegacyId = legacyId;
            adopted.Add(row);
        }

        return adopted;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.CreateAsync" />
    /// <remarks>Virtual: <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/> overrides it to upsert in place.</remarks>
    public virtual async Task<TModel> CreateAsync(TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await InsertAsync(ctx, ToEntity(model), origin, ct);
    }

    /// <summary>
    /// The insert tail both single-create paths share: the LegacyId guard
    /// <see cref="BulkCreateAsync"/> applies to its insert set, the insert itself, dedup linking,
    /// and the create broadcast. An unlinked row is invisible to every later match, so another
    /// source's copy of it is never recognised as a duplicate. Legacy treatment creates arrive here
    /// one record at a time.
    /// </summary>
    /// <exception cref="RecreationBlockedException">
    /// The LegacyId is held by a stored row, per
    /// <see cref="SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync{TEntity}(NocturneDbContext, IEnumerable{TEntity}, CancellationToken)"/>.
    /// </exception>
    protected async Task<TModel> InsertAsync(
        NocturneDbContext ctx, TEntity entity, WriteOrigin origin, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(entity.LegacyId)
            && (await ctx.GetBlockingLegacyIdsAsync<TEntity>([entity], ct)).Held.Count > 0)
        {
            throw new RecreationBlockedException(typeof(TModel).Name, RecreationBlockedException.LegacyIdIdentity(entity.LegacyId));
        }

        ctx.Set<TEntity>().Add(entity);
        await ctx.SaveChangesAsync(ct);
        await PostCommitDedupAsync(ctx, [entity], origin, ct);
        var created = ToDomain(entity);
        await RaiseBroadcastAsync([created], [], [], origin, ct);
        return created;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.UpdateAsync" />
    /// <remarks>
    /// Broadcasts only when the update is materially changed (same <see cref="HasMaterialChange"/>
    /// predicate <see cref="BulkCreateAsync"/> applies to its upsert split), captured before
    /// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(CancellationToken)"/>
    /// clears the change tracker's modified flags. This is the single-record twin of the bulk path's
    /// gate: decomposers idempotently re-upsert by LegacyId on every re-poll of a connector's catch-up
    /// overlap window, and without this gate a byte-identical re-send broadcasts as if it were a real
    /// change.
    /// </remarks>
    public async Task<TModel> UpdateAsync(Guid id, TModel model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>().FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"{typeof(TModel).Name} {id} not found");
        ApplyUpdate(entity, model);
        var materiallyChanged = HasMaterialChange(ctx, entity);
        await ctx.SaveChangesAsync(ct);
        var updated = ToDomain(entity);
        if (materiallyChanged)
            await RaiseBroadcastAsync([], [updated], [], origin, ct);
        return updated;
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{T}.BulkUpsertByLegacyIdAsync" />
    /// <remarks>
    /// The batch twin of <see cref="GetByLegacyIdAsync"/> followed by <see cref="CreateAsync"/> or
    /// <see cref="UpdateAsync"/> per record, with the same soft-delete visibility (the stored-row query
    /// runs under the context's filters), the same recreation guard, and the same
    /// <see cref="HasMaterialChange"/> gate on the update broadcast. The gate decides the broadcast
    /// only: the save always runs, because a change the gate does not count — a correlation id
    /// converging onto its anchor's — still has to reach the row. Change detection runs once over the
    /// batch and stays off through the save, the contract
    /// <see cref="NocturneDbContext.SaveChangesAsync(CancellationToken)"/> honours for a caller that
    /// has already detected. Inserted rows go through <see cref="PostCommitDedupAsync"/> as
    /// <see cref="BulkCreateAsync"/>'s do, so the dedup participants keyed by legacy id alone link
    /// their canonical groups on this path too.
    /// </remarks>
    public virtual async Task<LegacyUpsertBatch<TModel>> BulkUpsertByLegacyIdAsync(
        IReadOnlyList<TModel> records,
        WriteOrigin origin,
        bool preserveStoredCorrelationId = false,
        CancellationToken ct = default)
    {
        var byLegacyId = new Dictionary<string, TModel>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(record.LegacyId))
                byLegacyId[record.LegacyId] = record;
        }

        var outcomes = new Dictionary<string, LegacyUpsert<TModel>>(StringComparer.Ordinal);
        if (byLegacyId.Count == 0)
            return new LegacyUpsertBatch<TModel>(outcomes, 0);

        await using var ctx = await ContextFactory.CreateAsync(ct);

        var legacyIds = byLegacyId.Keys.ToList();
        var stored = await ctx.Set<TEntity>()
            .Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId))
            .ToListAsync(ct);
        var storedByLegacyId = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        foreach (var entity in stored)
            storedByLegacyId.TryAdd(entity.LegacyId!, entity);

        var inserted = new List<(string LegacyId, TEntity Entity)>();
        var updated = new List<(string LegacyId, TEntity Entity)>();
        foreach (var (legacyId, model) in byLegacyId)
        {
            if (storedByLegacyId.TryGetValue(legacyId, out var entity))
            {
                if (preserveStoredCorrelationId
                    && entity.CorrelationId is { } storedCorrelationId
                    && storedCorrelationId != Guid.Empty)
                {
                    model.CorrelationId = storedCorrelationId;
                }

                model.Id = entity.Id;
                ApplyUpdate(entity, model);
                updated.Add((legacyId, entity));
            }
            else
            {
                inserted.Add((legacyId, ToEntity(model)));
            }
        }

        var skippedDeleted = 0;
        if (inserted.Count > 0)
        {
            var blocked = await ctx.GetBlockingLegacyIdsAsync(inserted.Select(i => i.Entity), ct);
            skippedDeleted = inserted.Count(i => blocked.DeletedByUser.Contains(i.LegacyId));
            inserted.RemoveAll(i => blocked.Held.Contains(i.LegacyId));
            ctx.Set<TEntity>().AddRange(inserted.Select(i => i.Entity));
        }

        var materiallyChanged = new List<TEntity>();
        var autoDetect = ctx.ChangeTracker.AutoDetectChangesEnabled;
        ctx.ChangeTracker.DetectChanges();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            materiallyChanged.AddRange(updated.Select(u => u.Entity).Where(e => HasMaterialChange(ctx, e)));
            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        await PostCommitDedupAsync(ctx, inserted.Select(i => i.Entity).ToList(), origin, ct);

        foreach (var (legacyId, entity) in inserted)
            outcomes[legacyId] = new LegacyUpsert<TModel>(ToDomain(entity), Created: true);
        foreach (var (legacyId, entity) in updated)
            outcomes[legacyId] = new LegacyUpsert<TModel>(ToDomain(entity), Created: false);

        await RaiseBroadcastAsync(
            inserted.Select(i => outcomes[i.LegacyId].Record).ToList(),
            materiallyChanged.Select(ToDomain).ToList(),
            [],
            origin, ct);

        Logger.LogSkippedDeleted(typeof(TModel).Name, skippedDeleted);
        return new LegacyUpsertBatch<TModel>(outcomes, skippedDeleted);
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
        var entity = await ctx.RestoreDeletedAsync<TEntity>(id, typeof(TModel).Name, ct);
        // A restored record reappears in the dataset: broadcast it as a create so clients re-add it.
        var restored = ToDomain(entity);
        await RaiseBroadcastAsync([restored], [], [], origin, ct);
        return restored;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.BulkRestoreAsync" />
    public async Task<BulkRestoreResult<TModel>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var result = (await ctx.RestoreDeletedAsync<TEntity>(ids, typeof(TModel).Name, ct)).Map(ToDomain);
        await RaiseBroadcastAsync(result.Restored, [], [], origin, ct);
        return result;
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.GetDeletedAsync" />
    public async Task<IEnumerable<TModel>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return (await ctx.GetDeletedAsync<TEntity>(limit, offset, ct)).Select(ToDomain);
    }

    /// <inheritdoc cref="Core.Contracts.V4.Repositories.IV4Repository{T}.CountDeletedAsync" />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.CountDeletedAsync<TEntity>(ct);
    }

    /// <summary>
    /// The <see cref="RecordType"/> this repository's rows are linked under, or null for a type that
    /// does not participate in deduplication.
    /// </summary>
    protected internal virtual RecordType? DedupRecordType => null;

    /// <summary>
    /// Applies <see cref="ReadVisibilityFilter.ExcludeNonPrimary{TEntity}"/> for
    /// <see cref="DedupRecordType"/>. Every read path of a dedup participant routes through this so
    /// its counts and its rows agree.
    /// </summary>
    protected IQueryable<TEntity> ApplyReadVisibility(IQueryable<TEntity> query, NocturneDbContext ctx) =>
        DedupRecordType is { } recordType ? query.ExcludeNonPrimary(ctx, recordType) : query;

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
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx, ctx.Set<TEntity>().Where(e => e.LegacyId == legacyId), $"legacy_id={legacyId}", origin, ct);
    }

    /// <summary>
    /// Audited soft-delete of <paramref name="rows"/>, with <paramref name="scope"/> naming the key
    /// the delete was issued against on the audit row.
    /// </summary>
    /// <returns>The number of rows soft-deleted.</returns>
    protected async Task<int> AuditedSoftDeleteAndBroadcastAsync(
        NocturneDbContext ctx, IQueryable<TEntity> rows, string scope, WriteOrigin origin, CancellationToken ct)
    {
        var result = await ctx.AuditedSoftDeleteWithEntitiesAsync(rows, AuditContext, scope, ct);

        if (result.Collapsed)
            await RaiseBulkDeleteBroadcastAsync(result.Count, origin, ct);
        else
            await RaiseBroadcastAsync([], [], result.Entities.Select(ToDomain).ToList(), origin, ct);

        return result.Count;
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.GetModifiedSinceAsync" />
    /// <remarks>
    /// Pages on <c>sys_updated_at</c>, the column <see cref="ToDomain"/> reports as
    /// <see cref="IV4Record.ModifiedAt"/>, through <see cref="HistoryPage"/>, under the same
    /// <see cref="ApplyReadVisibility"/> every other read of this type observes.
    /// </remarks>
    public async Task<IReadOnlyList<TModel>> GetModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await HistoryPage.GetAsync(
            ApplyReadVisibility(ctx.Set<TEntity>().AsNoTracking(), ctx),
            e => e.SysUpdatedAt,
            e => e.Id,
            cursorMills,
            limit,
            Logger,
            typeof(TModel).Name,
            ct);

        return entities.Select(ToDomain).ToList();
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
        List<TEntity> ToInsert,
        int SkippedDeleted);

    /// <summary>Upsert participants override: match existing rows by their key, update them in place, and
    /// return the upserted rows, those that changed materially, and the rows still to insert.
    /// Default: nothing upserted.</summary>
    protected virtual Task<UpsertSplit> SplitUpsertsAsync(
        NocturneDbContext ctx, List<TEntity> entities, CancellationToken ct)
        => Task.FromResult(new UpsertSplit([], [], entities, 0));

    /// <summary>DeduplicationService participants override: link the just-inserted rows into canonical groups
    /// (runs AFTER commit). Default: no-op.</summary>
    protected virtual Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<TEntity> inserted, WriteOrigin origin, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Bulk write in one transaction: <see cref="SplitUpsertsAsync"/> separates the rows upserted in
    /// place from the rows still to insert, the insert set is deduplicated by LegacyId (batch- then
    /// DB-level) and inserted in chunks, and the commit is followed by dedup linking and the
    /// broadcast. The base implements the LegacyId-only path; SyncId-upsert / DeduplicationService
    /// participants override the <see cref="SplitUpsertsAsync"/> / <see cref="PostCommitDedupAsync"/>
    /// hooks rather than the whole method. A record whose legacy id a live row already carries is
    /// skipped; <see cref="BulkUpsertAsync"/> updates that row instead.
    /// </summary>
    public Task<BulkWrite<TModel>> BulkCreateAsync(
        IEnumerable<TModel> records, WriteOrigin origin, CancellationToken ct = default)
        => BulkWriteAsync(records.ToList(), origin, updateByLegacyId: false, ct);

    /// <inheritdoc cref="IBulkUpsertRepository{TRecord}.BulkUpsertAsync" />
    /// <remarks>
    /// The legacy-id match runs ahead of <see cref="SplitUpsertsAsync"/>, as the single path's
    /// <see cref="GetByLegacyIdAsync"/> runs ahead of <see cref="CreateAsync"/>, and under the same
    /// soft-delete visibility, so a legacy id held only by a user-deleted row still falls through to
    /// the recreation guard.
    /// </remarks>
    public Task<BulkWrite<TModel>> BulkUpsertAsync(
        IEnumerable<TModel> records, WriteOrigin origin, CancellationToken ct = default)
        => BulkWriteAsync(records.ToList(), origin, updateByLegacyId: true, ct);

    /// <summary>
    /// The body of <see cref="BulkCreateAsync"/> and <see cref="BulkUpsertAsync"/>. Virtual so a type
    /// that reacts to every bulk write hooks both at once.
    /// </summary>
    protected virtual async Task<BulkWrite<TModel>> BulkWriteAsync(
        List<TModel> records, WriteOrigin origin, bool updateByLegacyId, CancellationToken ct)
    {
        if (records.Count == 0) return [];
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var written = await ctx.ExecuteInTransactionAsync(
            async token =>
            {
                var legacy = updateByLegacyId
                    ? await SplitLegacyIdUpdatesAsync(ctx, records, token)
                    : new UpsertSplit([], [], records.Select(ToEntity).ToList(), 0);

                var split = await SplitUpsertsAsync(ctx, legacy.ToInsert, token);

                // The entity overload, not the key set: each legacy id's first candidate is both the one
                // InsertUnblockedAsync keeps and the one whose client id the tombstone exemption reads.
                var (toInsert, blockedSkipped) = await ctx.InsertUnblockedAsync(
                    split.ToInsert,
                    e => e.LegacyId,
                    (_, t) => ctx.GetBlockingLegacyIdsAsync(split.ToInsert, t),
                    token);

                var upserts = new UpsertSplit(
                    [.. legacy.UpdatedInPlace, .. split.UpdatedInPlace],
                    [.. legacy.MateriallyChanged, .. split.MateriallyChanged],
                    toInsert,
                    split.SkippedDeleted + blockedSkipped);
                return (upserts, toInsert);
            },
            (attempt, token) => attempt.toInsert.Count > 0
                ? ctx.AnyLandedAsync(attempt.toInsert, token)
                : ctx.AnyUpdateLandedAsync(attempt.upserts.MateriallyChanged, token),
            ct: ct);

        var (upserts, inserted) = written;
        if (inserted.Count > 0 || upserts.UpdatedInPlace.Count > 0)
        {
            await PostCommitDedupAsync(ctx, inserted, origin, ct);
            // Inserts broadcast as create; upserts broadcast as update only when materially changed
            // (a connector re-poll of byte-identical rows changes nothing, so it stays silent).
            await RaiseBroadcastAsync(
                inserted.Select(ToDomain).ToList(),
                upserts.MateriallyChanged.Select(ToDomain).ToList(),
                [],
                origin, ct);
        }

        Logger.LogSkippedDeleted(typeof(TModel).Name, upserts.SkippedDeleted);
        var updated = upserts.UpdatedInPlace.Select(ToDomain).ToList();
        return new BulkWrite<TModel>([.. updated, .. inserted.Select(ToDomain)], upserts.SkippedDeleted)
        {
            Updated = updated,
        };
    }

    /// <summary>
    /// Updates in place the live rows carrying a record's legacy id, the last record winning for a
    /// legacy id repeated in the batch, and returns the rest for the insert path. Persists the
    /// updates before returning, as <see cref="SplitUpsertsAsync"/> does, so the insert loop cannot
    /// lose them.
    /// </summary>
    /// <remarks>
    /// A stored <see cref="IDeviceAttributed.PatientDeviceId"/> is carried onto a record that
    /// resolved none, so a re-send whose attribution has since become ambiguous cannot unattribute
    /// the row; the single path does the same through <c>DecomposerBase.StampAttributionAsync</c>.
    /// </remarks>
    private async Task<UpsertSplit> SplitLegacyIdUpdatesAsync(
        NocturneDbContext ctx, List<TModel> records, CancellationToken ct)
    {
        var lastByLegacyId = new Dictionary<string, TModel>(StringComparer.Ordinal);
        foreach (var record in records.Where(r => !string.IsNullOrEmpty(r.LegacyId)))
            lastByLegacyId[record.LegacyId!] = record;

        var storedByLegacyId = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        if (lastByLegacyId.Count > 0)
        {
            var legacyIds = lastByLegacyId.Keys.ToList();
            var stored = await ctx.Set<TEntity>()
                .Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId))
                .ToListAsync(ct);
            foreach (var entity in stored)
                storedByLegacyId.TryAdd(entity.LegacyId!, entity);
        }

        var updated = new List<TEntity>();
        var materiallyChanged = new List<TEntity>();
        var toInsert = new List<TEntity>();
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.LegacyId))
            {
                toInsert.Add(ToEntity(record));
                continue;
            }

            if (!ReferenceEquals(lastByLegacyId[record.LegacyId], record))
                continue;

            if (!storedByLegacyId.TryGetValue(record.LegacyId, out var entity))
            {
                toInsert.Add(ToEntity(record));
                continue;
            }

            if (record is IDeviceAttributed { PatientDeviceId: null } attributed
                && ToDomain(entity) is IDeviceAttributed storedAttribution)
            {
                attributed.PatientDeviceId = storedAttribution.PatientDeviceId;
            }

            record.Id = entity.Id;
            ApplyUpdate(entity, record);
            updated.Add(entity);
            if (HasMaterialChange(ctx, entity))
                materiallyChanged.Add(entity);
        }

        if (updated.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return new UpsertSplit(updated, materiallyChanged, toInsert, 0);
    }
}
