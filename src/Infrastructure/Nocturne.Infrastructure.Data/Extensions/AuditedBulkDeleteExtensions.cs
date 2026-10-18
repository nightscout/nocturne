using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The outcome of an audited soft delete: how many rows were soft-deleted, the entities
/// materialized for the caller's realtime broadcast, and the duplicates promoted in their place.
/// </summary>
/// <param name="Count">Rows soft-deleted by the operation.</param>
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

    /// <summary>What a <c>bulk_delete</c> summary row records in place of per-row snapshots.</summary>
    private readonly record struct BulkDeleteSummary(int Count, string Scope);

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

                var deleted = await query
                    .Where(e => ids.Contains(EF.Property<Guid>(e, "Id")))
                    .ExecuteDeleteAsync(token);
                return (Count: deleted, FirstId: ids[0]);
            },
            async (attempt, token) => attempt.Count > 0
                && !await context.Set<T>().IgnoreQueryFilters()
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
    /// <returns>The number of records soft-deleted.</returns>
    public static async Task<int> AuditedSoftDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default,
        DuplicateDelete duplicates = DuplicateDelete.PromoteSurvivor) where T : class, IAuditable, ISoftDeletable
    {
        var (deleted, _) = await context.ExecuteInTransactionAsync(
            async token =>
            {
                var deletedAt = NocturneDbContext.UtcNowAtStoredPrecision();
                var rows = await ScopeAsync(context, query, duplicates, token);
                var deleted = await SoftDeleteRowsAsync(context, rows, auditContext, deletedAt, duplicates, token);
                await WriteBulkDeleteSummaryAsync<T>(context, deleted.Count, scope, auditContext, token);
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
    /// entities are loaded anyway, so each gets its own <c>delete</c> audit row; over it nothing is
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
            var deletedAt = NocturneDbContext.UtcNowAtStoredPrecision();
            var rows = await ScopeAsync(context, query, duplicates, token);
            // One row past the cap is all it takes to know the match set exceeds it.
            var records = await rows.Take(BroadcastMaterializationCap + 1).ToListAsync(token);
            var collapsed = records.Count > BroadcastMaterializationCap;

            List<MutationAuditLogEntity> auditEntries =
                collapsed ? [] : BuildDeleteAuditEntries(context, records, auditContext);

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

            var deleted = await SoftDeleteRowsAsync(context, rows, auditContext, deletedAt, duplicates, token);

            if (collapsed)
                await WriteBulkDeleteSummaryAsync<T>(context, deleted.Count, scope, auditContext, token);

            return (Result: new AuditedSoftDeleteResult<T>(deleted.Count, records, deleted.Promoted), DeletedAt: deletedAt);
        },
        (attempt, token) => SoftDeleteLandedAsync<T>(context, attempt.Result.Count, attempt.DeletedAt, token),
        ct: ct);
        return result;
    }

    /// <summary>
    /// Whether a soft delete whose commit reported failure landed: its rows carry its stamp, taken at
    /// <see cref="NocturneDbContext.UtcNowAtStoredPrecision"/> so that it compares equal to the stored one. With
    /// nothing deleted there is nothing to report, and the work runs again.
    /// </summary>
    private static async Task<bool> SoftDeleteLandedAsync<T>(
        NocturneDbContext context, int count, DateTime deletedAt, CancellationToken ct)
        where T : class, ISoftDeletable
    {
        if (count == 0)
            return false;
        var stamped = context.Set<T>().IgnoreQueryFilters().Where(e => e.DeletedAt == deletedAt);
        if (context.Model.FindEntityType(typeof(T))?.FindProperty(nameof(ITenantScoped.TenantId)) is not null)
            stamped = stamped.Where(e => EF.Property<Guid>(e, nameof(ITenantScoped.TenantId)) == context.TenantId);
        return await stamped.AnyAsync(ct);
    }

    /// <summary>
    /// Stamps <c>DeletedAt</c> and the dedup attribution flag: a user-initiated delete blocks resync
    /// re-creation, a system sweep leaves the row re-creatable (<see cref="SoftDeleteDedupExtensions"/>).
    /// Runs whether or not an audit row is written. Then, under
    /// <see cref="DuplicateDelete.PromoteSurvivor"/>, repoints the duplicate groups whose primary it
    /// deleted, stamping each promoted copy after the deletes.
    /// </summary>
    /// <remarks>
    /// A delete is a write a v3 history client has to be told of, so it moves the row's update stamp
    /// as a tracked save would (<c>SysUpdatedAt</c> on an <see cref="ISystemTimestamped"/> row,
    /// <c>UpdatedAt</c> on an <see cref="IEntityTimestamped"/> one), and like one it spreads the rows
    /// over successive milliseconds,
    /// <see cref="NocturneDbContext.SystemTimestampGroupSize"/> to each (see <see cref="HistoryPage"/>).
    /// </remarks>
    private static async Task<(int Count, IReadOnlyList<Guid> Promoted)> SoftDeleteRowsAsync<T>(
        NocturneDbContext context,
        IQueryable<T> query,
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
                    .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete), ct);
            return (updated, []);
        }

        // The match set is read once and then updated by primary key a group at a time: re-running
        // the filtered, ordered read for every group would rescan the remaining set each time.
        var live = query.Where(e => e.DeletedAt == null);
        var ids = await live.Select(e => EF.Property<Guid>(e, "Id")).OrderBy(id => id).ToListAsync(ct);
        var groups = ids.Chunk(NocturneDbContext.SystemTimestampGroupSize).ToList();
        var total = 0;
        foreach (var (group, index) in groups.Select((g, i) => (g, i)))
        {
            var stamp = deletedAt.AddMilliseconds(index);
            var rows = live.Where(e => group.Contains(EF.Property<Guid>(e, "Id")));
            total += stampColumn switch
            {
                nameof(ISystemTimestamped.SysUpdatedAt) => await rows.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(e => e.DeletedAt, deletedAt)
                        .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete)
                        .SetProperty(e => EF.Property<DateTime>(e, nameof(ISystemTimestamped.SysUpdatedAt)), stamp),
                    ct),
                nameof(IEntityTimestamped.UpdatedAt) => await rows.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(e => e.DeletedAt, deletedAt)
                        .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete)
                        .SetProperty(e => EF.Property<DateTime>(e, nameof(IEntityTimestamped.UpdatedAt)), stamp),
                    ct),
                _ => await rows.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(e => e.DeletedAt, deletedAt)
                        .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete),
                    ct),
            };
        }

        IReadOnlyList<Guid> promoted = recordType is { } type && duplicates == DuplicateDelete.PromoteSurvivor
            ? await DuplicateGroupPrimaries.RepointAwayFromAsync(
                context, type, ids, deletedAt.AddMilliseconds(groups.Count), ct)
            : [];
        return (total, promoted);
    }

    /// <summary>
    /// The rows a delete covers: <paramref name="query"/>, and under
    /// <see cref="DuplicateDelete.EveryCopy"/> every other copy in the duplicate groups it matches.
    /// </summary>
    private static async Task<IQueryable<T>> ScopeAsync<T>(
        NocturneDbContext context, IQueryable<T> query, DuplicateDelete duplicates, CancellationToken ct)
        where T : class, ISoftDeletable =>
        duplicates == DuplicateDelete.EveryCopy
            ? await DuplicateGroupPrimaries.WithGroupMatesAsync(context, query, ct)
            : query;

    /// <summary>
    /// Appends the one summary row a bulk soft delete records: the entity type, how many rows it
    /// covered, the scope it was issued against, and the actor. <c>EntityId</c> is null — the row
    /// describes a set, not a record.
    /// </summary>
    private static async Task WriteBulkDeleteSummaryAsync<T>(
        NocturneDbContext context,
        int count,
        string scope,
        IAuditContext? auditContext,
        CancellationToken ct)
    {
        if (count == 0 || auditContext.IsSystemMutation())
            return;

        context.Set<MutationAuditLogEntity>().Add(new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            EntityType = AuditEntityTypeName<T>(),
            EntityId = null,
            Action = "bulk_delete",
            ChangesJson = JsonSerializer.Serialize(new BulkDeleteSummary(count, scope), JsonOptions),
            SubjectId = auditContext?.SubjectId,
            SubjectName = auditContext?.SubjectName,
            AuthType = auditContext?.AuthType,
            IpAddress = auditContext?.IpAddress,
            TokenId = auditContext?.TokenId,
            TraceId = auditContext?.TraceId,
            Endpoint = auditContext?.Endpoint,
            CreatedAt = DateTime.UtcNow
        });

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

            return new MutationAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                EntityType = entityTypeName,
                EntityId = (Guid)entry.Property("Id").CurrentValue!,
                Action = "delete",
                ChangesJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                SubjectId = auditContext?.SubjectId,
                SubjectName = auditContext?.SubjectName,
                AuthType = auditContext?.AuthType,
                IpAddress = auditContext?.IpAddress,
                TokenId = auditContext?.TokenId,
                TraceId = auditContext?.TraceId,
                Endpoint = auditContext?.Endpoint,
                CreatedAt = now
            };
        }).ToList();
    }

    private static string AuditEntityTypeName<T>() => typeof(T).Name.Replace("Entity", "");

    private static readonly ConcurrentDictionary<Type, PropertyInfo> IdProperties = new();

    private static Guid IdOf<T>(T entity) where T : class
        => (Guid)IdProperties.GetOrAdd(typeof(T), t => t.GetProperty("Id")!).GetValue(entity)!;
}
