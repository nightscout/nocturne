using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The outcome of an audited soft delete: how many rows were soft-deleted, the entities
/// materialized for the caller's realtime broadcast, and the duplicates promoted in their place.
/// </summary>
/// <param name="Count">
/// Rows soft-deleted by the operation that the caller's history clamp shows it. A copy the clamp hides
/// is deleted and audited too, and left out.
/// </param>
/// <param name="Entities">
/// The soft-deleted entities, detached but still holding their loaded values — empty when the match
/// set exceeded <see cref="AuditedBulkDeleteExtensions.BroadcastMaterializationCap"/>.
/// </param>
/// <param name="Promoted">
/// The ids of the live copies made their duplicate group's primary because the group's primary was
/// deleted (<see cref="DuplicateGroupPrimaries"/>). Normal reads show them from now on.
/// </param>
public readonly record struct AuditedSoftDeleteResult<T>(int Count, List<T> Entities, IReadOnlyList<Guid> Promoted)
{
    /// <summary>
    /// True when the match set was too large to materialize: <see cref="Entities"/> is empty and the
    /// caller must fall back to a coarse collection-level invalidation instead of per-record events.
    /// </summary>
    public bool Collapsed => Count > 0 && Entities.Count == 0;
}

/// <summary>
/// Extensions for executing bulk deletes that record them in <c>mutation_audit_log</c>.
/// </summary>
/// <remarks>
/// A soft delete leaves the row in place, so a per-row snapshot would be a verbatim copy of data that
/// is still readable and the dedup discriminator lives on the row itself
/// (<see cref="SoftDeleteDedupExtensions"/>) — <see cref="AuditedSoftDeleteAsync{T}"/> therefore
/// records one <c>bulk_delete</c> summary row naming the scope it was issued against. A caller that
/// needs per-record realtime events has to materialize the rows anyway, so
/// <see cref="AuditedSoftDeleteWithEntitiesAsync{T}"/> spends the snapshot it has already loaded and
/// writes a row each, collapsing to the summary only past
/// <see cref="BroadcastMaterializationCap"/>. A hard delete destroys the row, so its per-row snapshots
/// are the only surviving copy and are kept.
/// <para>
/// A soft delete of a type that takes part in deduplication either moves the primary of every group
/// whose primary it deleted onto a live copy, or deletes every copy in the groups it touches
/// (<see cref="DuplicateDelete"/>), in the same transaction, so no delete path leaves the other
/// sources' copies hidden behind a deleted primary.
/// </para>
/// </remarks>
public static class AuditedBulkDeleteExtensions
{
    /// <summary>
    /// Rows an audited hard delete snapshots per page. Sized so one page's JSON snapshots are a bounded
    /// working set while the statement count stays proportional to rows/1000 rather than to rows.
    /// </summary>
    private const int HardDeletePageSize = 1000;

    /// <summary>
    /// Upper bound on the entities an audited soft delete materializes for its caller's realtime
    /// broadcast. A per-record event stream longer than this is worse for subscribers than one coarse
    /// invalidation, and materializing a source's whole history is what this cap exists to prevent.
    /// </summary>
    public const int BroadcastMaterializationCap = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>What a <c>bulk_delete</c> or <c>allow_reimport</c> summary row records in place of per-row snapshots.</summary>
    private readonly record struct BulkSummary(int Count, string Scope);

    /// <summary>
    /// Executes a bulk hard delete, snapshotting every removed row into <c>mutation_audit_log</c>.
    /// </summary>
    /// <remarks>
    /// Runs a page at a time: each page's snapshots and its delete share a transaction, but the
    /// operation as a whole is NOT atomic — a failure part-way leaves earlier pages deleted (and
    /// audited). Re-running the same call resumes, because the deleted rows no longer match.
    /// Unattributed/system deletes skip the snapshots entirely and issue a single delete statement.
    /// </remarks>
    public static async Task<int> AuditedExecuteDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct = default) where T : class, IAuditable
    {
        if (auditContext.IsSystemMutation())
            return await query.ExecuteDeleteAsync(ct);

        var total = 0;
        int page;

        do
        {
            (page, _) = await context.ExecuteInTransactionAsync(async token =>
            {
                var records = await query.Take(HardDeletePageSize).ToListAsync(token);
                if (records.Count == 0)
                    return (Count: 0, FirstId: Guid.Empty);

                var auditEntries = BuildDeleteAuditEntries(context, records, auditContext);
                var ids = records.Select(IdOf).ToList();

                // Detach the loaded entities so they don't interfere with the bulk delete
                foreach (var record in records)
                    context.Entry(record).State = EntityState.Detached;

                context.Set<MutationAuditLogEntity>().AddRange(auditEntries);
                await context.SaveChangesAsync(token);

                var deleted = await context.Set<T>().IncludingDeleted()
                    .Where(e => ids.Contains(EF.Property<Guid>(e, "Id")))
                    .ExecuteDeleteAsync(token);
                return (Count: deleted, FirstId: ids[0]);
            },
            async (attempt, token) => attempt.Count > 0
                && !await context.Set<T>().IncludingDeleted()
                    .AnyAsync(e => EF.Property<Guid>(e, "Id") == attempt.FirstId, token),
            ct: ct);

            total += page;
        }
        while (page == HardDeletePageSize);

        return total;
    }

    /// <summary>
    /// Executes a bulk soft delete, recording it as one <c>bulk_delete</c> summary row for
    /// <paramref name="scope"/>. No rows are materialized.
    /// </summary>
    /// <param name="context">The tenant-scoped context the audit row is written on.</param>
    /// <param name="query">The rows to soft-delete.</param>
    /// <param name="auditContext">Actor/request metadata; system or null writes no audit row.</param>
    /// <param name="scope">
    /// The key the delete was issued against (e.g. <c>data_source=dexcom-connector</c>), recorded on
    /// the summary row — without it the row cannot say which records it covered.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="duplicates">What happens to the other copies in the deleted rows' duplicate groups.</param>
    /// <returns>
    /// The number of records soft-deleted that the caller's history clamp shows it. The summary row
    /// counts every record deleted.
    /// </returns>
    public static async Task<int> AuditedSoftDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default,
        DuplicateDelete duplicates = DuplicateDelete.PromoteSurvivor) where T : class, ISoftDeletable
    {
        var (deleted, _) = await context.ExecuteInTransactionAsync(
            async token =>
            {
                var deletedAt = await HistoryHorizon.StampAsync(context, token);
                var (rows, hidden) = await ScopeAsync(context, query, duplicates, token);
                var deleted = await SoftDeleteRowsAsync(context, rows, hidden, auditContext, deletedAt, duplicates, token);
                await WriteBulkDeleteSummaryAsync<T>(context, deleted.Count + deleted.Hidden, scope, auditContext, token);
                return (count: deleted.Count, deletedAt);
            },
            (attempt, token) => SoftDeleteLandedAsync<T>(context, attempt.count, attempt.deletedAt, token),
            ct: ct);
        return deleted;
    }

    /// <summary>
    /// As <see cref="AuditedSoftDeleteAsync{T}"/>, but returns the ids of the soft-deleted records so the
    /// caller can broadcast per-record delete events.
    /// </summary>
    public static async Task<AuditedSoftDeleteResult<Guid>> AuditedSoftDeleteWithIdsAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default,
        DuplicateDelete duplicates = DuplicateDelete.PromoteSurvivor) where T : class, IAuditable, ISoftDeletable
    {
        var result = await context.AuditedSoftDeleteWithEntitiesAsync(query, auditContext, scope, ct, duplicates);
        return new AuditedSoftDeleteResult<Guid>(
            result.Count, result.Entities.Select(IdOf).ToList(), result.Promoted);
    }

    /// <summary>
    /// As <see cref="AuditedSoftDeleteAsync{T}"/>, but returns the soft-deleted entities so the caller can
    /// project them (e.g. to the legacy <c>Entry</c> shape) and broadcast per-record delete events.
    /// </summary>
    /// <remarks>
    /// Materializes at most <see cref="BroadcastMaterializationCap"/> entities. Under the cap the
    /// entities are loaded anyway, so each gets its own <c>delete</c> audit row, as does each copy the
    /// caller's history clamp hides (<see cref="HiddenDeleteAuditEntriesAsync{T}"/>); over it nothing is
    /// returned and the operation records a single <c>bulk_delete</c> summary row for
    /// <paramref name="scope"/>, as <see cref="AuditedSoftDeleteAsync{T}"/> does.
    /// </remarks>
    public static async Task<AuditedSoftDeleteResult<T>> AuditedSoftDeleteWithEntitiesAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default,
        DuplicateDelete duplicates = DuplicateDelete.PromoteSurvivor) where T : class, IAuditable, ISoftDeletable
    {
        var (result, _) = await context.ExecuteInTransactionAsync(async token =>
        {
            var deletedAt = await HistoryHorizon.StampAsync(context, token);
            var (rows, hidden) = await ScopeAsync(context, query, duplicates, token);
            // One row past the cap is all it takes to know the match set exceeds it.
            var records = await rows.Take(BroadcastMaterializationCap + 1).ToListAsync(token);
            var collapsed = records.Count > BroadcastMaterializationCap;

            List<MutationAuditLogEntity> auditEntries = collapsed
                ? []
                : [
                    .. BuildDeleteAuditEntries(context, records, auditContext),
                    .. await HiddenDeleteAuditEntriesAsync<T>(context, hidden, auditContext, token),
                ];

            // Detach the loaded entities so they don't interfere with the bulk update
            foreach (var record in records)
                context.Entry(record).State = EntityState.Detached;

            if (collapsed)
                records.Clear();

            if (auditEntries.Count > 0)
            {
                context.Set<MutationAuditLogEntity>().AddRange(auditEntries);
                await context.SaveChangesAsync(token);
            }

            var deleted = await SoftDeleteRowsAsync(context, rows, hidden, auditContext, deletedAt, duplicates, token);

            if (collapsed)
                await WriteBulkDeleteSummaryAsync<T>(context, deleted.Count + deleted.Hidden, scope, auditContext, token);

            return (Result: new AuditedSoftDeleteResult<T>(deleted.Count, records, deleted.Promoted), DeletedAt: deletedAt);
        },
        (attempt, token) => SoftDeleteLandedAsync<T>(context, attempt.Result.Count, attempt.DeletedAt, token),
        ct: ct);
        return result;
    }

    /// <summary>
    /// Whether a soft delete whose commit reported failure landed: its rows carry its stamp, taken by
    /// <see cref="HistoryHorizon.StampAsync"/> at the precision it is stored at, so that it compares
    /// equal to the stored one. With nothing deleted there is nothing to report, and the work runs again.
    /// </summary>
    private static async Task<bool> SoftDeleteLandedAsync<T>(
        NocturneDbContext context, int count, DateTime deletedAt, CancellationToken ct)
        where T : class, ISoftDeletable
    {
        if (count == 0)
            return false;
        return await context.Set<T>().IncludingDeleted()
            .AnyAsync(e => e.DeletedAt == deletedAt, ct);
    }

    /// <summary>
    /// Stamps <c>DeletedAt</c> and the dedup attribution flag: a user-initiated delete blocks resync
    /// re-creation, a system sweep leaves the row re-creatable (<see cref="SoftDeleteDedupExtensions"/>).
    /// Runs whether or not an audit row is written. Then, under
    /// <see cref="DuplicateDelete.PromoteSurvivor"/>, repoints the duplicate groups whose primary it
    /// deleted, stamping each promoted copy after the deletes; their links are locked before the rows
    /// are written (<see cref="DuplicateGroupPrimaries.LockGroupsOfAsync"/>), except for a glucose delete
    /// that recomputes inline, whose lock order <see cref="GlucoseStreamFlags"/> states.
    /// </summary>
    /// <remarks>
    /// A delete is a write a v3 history client has to be told of, so it moves the row's update stamp
    /// as a tracked save would (<c>SysUpdatedAt</c> on an <see cref="ISystemTimestamped"/> row,
    /// <c>UpdatedAt</c> on an <see cref="IEntityTimestamped"/> one), and like one it spreads the rows
    /// over successive milliseconds,
    /// <see cref="NocturneDbContext.SystemTimestampGroupSize"/> to each (see <see cref="HistoryPage"/>).
    /// <paramref name="hidden"/> are copies the caller's history clamp hides, written by id with the
    /// clamp lifted (<see cref="HistoryClampLiftExtensions"/>) and counted apart in <c>Hidden</c>: the
    /// count a caller is given is what its clamp shows it, and the audit records every row written.
    /// </remarks>
    private static async Task<(int Count, int Hidden, IReadOnlyList<Guid> Promoted)> SoftDeleteRowsAsync<T>(
        NocturneDbContext context,
        IQueryable<T> query,
        IReadOnlyList<Guid> hidden,
        IAuditContext? auditContext,
        DateTime deletedAt,
        DuplicateDelete duplicates,
        CancellationToken ct) where T : class, ISoftDeletable
    {
        var isUserDelete = !auditContext.IsSystemMutation();
        var stampColumn = typeof(ISystemTimestamped).IsAssignableFrom(typeof(T))
            ? nameof(ISystemTimestamped.SysUpdatedAt)
            : typeof(IEntityTimestamped).IsAssignableFrom(typeof(T))
                ? nameof(IEntityTimestamped.UpdatedAt)
                : null;
        var recordType = DuplicateGroupPrimaries.RecordTypeOf<T>();

        if (stampColumn is null && recordType is null)
        {
            var updated = await query.ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.DeletedAt, deletedAt)
                    .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), isUserDelete), ct);
            return (updated, 0, []);
        }

        // The match set is read once and each group is then stamped by its ids alone. An update that
        // joins back to the caller's selection can be planned as tenant rows times chunk when the
        // table's statistics are stale; by ids alone, a chunk costs at most one pass over the tenant's
        // rows (when the table has no statistics) and a primary key probe per id otherwise.
        var ids = await query.Where(e => e.DeletedAt == null)
            .Select(e => EF.Property<Guid>(e, "Id")).OrderBy(id => id).ToListAsync(ct);
        await context.PlanForEachArrayAsync(ids.Count + hidden.Count, ct);
        using var wide = typeof(T) == typeof(SensorGlucoseEntity)
            ? await GlucoseStreamFlags.DeferIfWideAsync(context, ids.Count + hidden.Count, ct)
            : null;
        if (recordType is { } linked && duplicates == DuplicateDelete.PromoteSurvivor)
            await DuplicateGroupPrimaries.LockGroupsOfAsync(context, linked, ids, ct);
        var groups = ids.Chunk(NocturneDbContext.SystemTimestampGroupSize).ToList();
        var unstamped = context.Set<T>().Where(e => e.DeletedAt == null);
        var total = await StampAsync(unstamped, groups, 0, ct);
        var hiddenTotal = 0;
        if (hidden.Count > 0)
        {
            var hiddenGroups = hidden.Order().Chunk(NocturneDbContext.SystemTimestampGroupSize).ToList();
            hiddenTotal = await context.WriteWithHistoryClampLiftedAsync(
                token => StampAsync(unstamped, hiddenGroups, groups.Count, token),
                ct);
        }

        List<Guid> written = [.. ids, .. hidden];
        if (typeof(T) == typeof(StateSpanEntity))
            await context.ReopenSupersededAsync(written, purged: false, ct);

        IReadOnlyList<Guid> promoted = recordType is { } type && duplicates == DuplicateDelete.PromoteSurvivor
            ? await DuplicateGroupPrimaries.RepointAwayFromAsync(
                context, type, ids, deletedAt.AddMilliseconds(groups.Count), ct)
            : [];
        if (typeof(T) == typeof(SensorGlucoseEntity))
            await GlucoseStreamFlags.AfterRowsChangedAsync(context, written, ct);
        return (total, hiddenTotal, promoted);

        async Task<int> StampAsync(IQueryable<T> source, List<Guid[]> idGroups, int firstGroup, CancellationToken token)
        {
            var stamped = 0;
            foreach (var (group, index) in idGroups.Select((g, i) => (g, firstGroup + i)))
            {
                var stamp = deletedAt.AddMilliseconds(index);
                var rows = source.Where(e => group.Contains(EF.Property<Guid>(e, "Id")));
                stamped += stampColumn switch
                {
                    nameof(ISystemTimestamped.SysUpdatedAt) => await rows.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(e => e.DeletedAt, deletedAt)
                            .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), isUserDelete)
                            .SetProperty(e => EF.Property<DateTime>(e, nameof(ISystemTimestamped.SysUpdatedAt)), stamp),
                        token),
                    nameof(IEntityTimestamped.UpdatedAt) => await rows.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(e => e.DeletedAt, deletedAt)
                            .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), isUserDelete)
                            .SetProperty(e => EF.Property<DateTime>(e, nameof(IEntityTimestamped.UpdatedAt)), stamp),
                        token),
                    _ => await rows.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(e => e.DeletedAt, deletedAt)
                            .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), isUserDelete),
                        token),
                };
            }

            return stamped;
        }
    }

    /// <summary>
    /// The rows a delete covers: <paramref name="query"/>, and under
    /// <see cref="DuplicateDelete.EveryCopy"/> every other copy in the duplicate groups it matches,
    /// with the copies the caller's history clamp hides apart
    /// (<see cref="DuplicateGroupPrimaries.WithGroupMatesAsync{T}"/>).
    /// </summary>
    private static async Task<(IQueryable<T> Rows, IReadOnlyList<Guid> Hidden)> ScopeAsync<T>(
        NocturneDbContext context, IQueryable<T> query, DuplicateDelete duplicates, CancellationToken ct)
        where T : class, ISoftDeletable =>
        duplicates == DuplicateDelete.EveryCopy
            ? await DuplicateGroupPrimaries.WithGroupMatesAsync(context, query, ct)
            : (query, []);

    /// <summary>
    /// The <c>delete</c> audit rows of <paramref name="hidden"/>, the copies the caller's history clamp
    /// hides, snapshotted as the rows it can see are: a delete is recorded whatever its caller could
    /// read. The rows are read with the clamp lifted
    /// (<see cref="DuplicateGroupPrimaries.HiddenRowsAsync{TEntity}"/>) and reach only the audit log.
    /// </summary>
    private static async Task<List<MutationAuditLogEntity>> HiddenDeleteAuditEntriesAsync<T>(
        NocturneDbContext context, IReadOnlyList<Guid> hidden, IAuditContext? auditContext, CancellationToken ct)
        where T : class, IAuditable
    {
        if (hidden.Count == 0 || auditContext.IsSystemMutation())
            return [];

        var rows = await DuplicateGroupPrimaries.HiddenRowsAsync(context, context.Set<T>(), hidden, [], ct);
        var entries = BuildDeleteAuditEntries(context, rows, auditContext);
        foreach (var row in rows)
            context.Entry(row).State = EntityState.Detached;
        return entries;
    }

    /// <summary>
    /// Clears <c>deleted_by_user</c> on the user tombstones among <paramref name="query"/>, so a resync
    /// re-creates their records (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>),
    /// and records one <c>allow_reimport</c> summary row for <paramref name="scope"/>. The rows stay
    /// deleted.
    /// </summary>
    /// <returns>The number of tombstones released.</returns>
    public static async Task<int> AuditedAllowReimportAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default) where T : class, ITenantScoped, ISoftDeletable
        => await context.ExecuteInTransactionAsync(async token =>
        {
            var released = await query.UserTombstones().ExecuteUpdateAsync(
                s => s.SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), false), token);
            await WriteBulkSummaryAsync<T>(context, "allow_reimport", released, scope, auditContext, token);
            return released;
        }, ct: ct);

    private static Task WriteBulkDeleteSummaryAsync<T>(
        NocturneDbContext context,
        int count,
        string scope,
        IAuditContext? auditContext,
        CancellationToken ct)
        => WriteBulkSummaryAsync<T>(context, "bulk_delete", count, scope, auditContext, ct);

    /// <summary>
    /// Appends the one summary row a bulk operation records: the entity type, how many rows it
    /// covered, the scope it was issued against, and the actor. <c>EntityId</c> is null — the row
    /// describes a set, not a record.
    /// </summary>
    private static async Task WriteBulkSummaryAsync<T>(
        NocturneDbContext context,
        string action,
        int count,
        string scope,
        IAuditContext? auditContext,
        CancellationToken ct)
    {
        if (count == 0 || auditContext.IsSystemMutation())
            return;

        context.Set<MutationAuditLogEntity>().Add(MutationAuditLogEntity.Create(
            context.TenantId,
            auditContext,
            AuditEntityTypeName<T>(),
            null,
            action,
            JsonSerializer.Serialize(new BulkSummary(count, scope), JsonOptions),
            DateTime.UtcNow));

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds one "delete" audit row per affected record, snapshotting its current values.
    /// Returns an empty list for system/unattributed mutations: these helpers write audit rows
    /// themselves rather than through <c>MutationAuditInterceptor</c>, so they have to apply the
    /// same skip — a connector's reconcile sweep is high-volume and has no human actor, and
    /// recording it added ~77k actorless rows a week to <c>mutation_audit_log</c> in production.
    /// </summary>
    private static List<MutationAuditLogEntity> BuildDeleteAuditEntries<T>(
        NocturneDbContext context,
        List<T> affectedRecords,
        IAuditContext? auditContext) where T : class, IAuditable
    {
        if (auditContext.IsSystemMutation())
            return [];

        var now = DateTime.UtcNow;
        var entityTypeName = AuditEntityTypeName<T>();

        return affectedRecords.Select(record =>
        {
            var entry = context.Entry(record);
            var snapshot = new Dictionary<string, object?>();

            foreach (var prop in entry.Properties)
            {
                if (prop.Metadata.IsPrimaryKey())
                    continue;

                var property = typeof(T).GetProperty(prop.Metadata.Name,
                    BindingFlags.Public | BindingFlags.Instance);

                if (property?.GetCustomAttribute<AuditIgnoredAttribute>() is not null)
                    continue;

                var isRedacted = property?.GetCustomAttribute<AuditRedactedAttribute>() is not null;
                snapshot[prop.Metadata.Name] = isRedacted ? "[redacted]" : prop.CurrentValue;
            }

            return MutationAuditLogEntity.Create(
                context.TenantId,
                auditContext,
                entityTypeName,
                (Guid)entry.Property("Id").CurrentValue!,
                "delete",
                JsonSerializer.Serialize(snapshot, JsonOptions),
                now);
        }).ToList();
    }

    private static string AuditEntityTypeName<T>() => typeof(T).Name.Replace("Entity", "");

    private static readonly ConcurrentDictionary<Type, PropertyInfo> IdProperties = new();

    private static Guid IdOf<T>(T entity) where T : class
        => (Guid)IdProperties.GetOrAdd(typeof(T), t => t.GetProperty("Id")!).GetValue(entity)!;
}
