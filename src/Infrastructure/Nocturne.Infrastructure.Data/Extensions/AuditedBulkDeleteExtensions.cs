using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The outcome of an audited soft delete: how many rows were soft-deleted, and the entities
/// materialized for the caller's realtime broadcast.
/// </summary>
/// <param name="Count">Rows soft-deleted by the operation.</param>
/// <param name="Entities">
/// The soft-deleted entities, detached but still holding their loaded values — empty when the match
/// set exceeded <see cref="AuditedBulkDeleteExtensions.BroadcastMaterializationCap"/>.
/// </param>
public readonly record struct AuditedSoftDeleteResult<T>(int Count, List<T> Entities)
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
/// (<see cref="SoftDeleteDedupExtensions"/>) — soft deletes therefore record one <c>bulk_delete</c>
/// summary row naming the scope they were issued against. A hard delete destroys the row, so its
/// per-row snapshots are the only surviving copy and are kept.
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

        var strategy = context.Database.CreateExecutionStrategy();
        var total = 0;
        int page;

        do
        {
            page = await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct);

                var records = await query.Take(HardDeletePageSize).ToListAsync(ct);
                if (records.Count == 0)
                {
                    await transaction.CommitAsync(ct);
                    return 0;
                }

                var auditEntries = BuildDeleteAuditEntries(context, records, auditContext);
                var ids = records.Select(IdOf).ToList();

                // Detach the loaded entities so they don't interfere with the bulk delete
                foreach (var record in records)
                    context.Entry(record).State = EntityState.Detached;

                context.Set<MutationAuditLogEntity>().AddRange(auditEntries);
                await context.SaveChangesAsync(ct);

                var deleted = await query
                    .Where(e => ids.Contains(EF.Property<Guid>(e, "Id")))
                    .ExecuteDeleteAsync(ct);

                await transaction.CommitAsync(ct);
                return deleted;
            });

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
    /// <returns>The number of records soft-deleted.</returns>
    public static async Task<int> AuditedSoftDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        string scope,
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var count = await SoftDeleteRowsAsync(query, auditContext, ct);
            await WriteBulkDeleteSummaryAsync<T>(context, count, scope, auditContext, ct);

            await transaction.CommitAsync(ct);
            return count;
        });
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
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
    {
        var result = await context.AuditedSoftDeleteWithEntitiesAsync(query, auditContext, scope, ct);
        return new AuditedSoftDeleteResult<Guid>(
            result.Count, result.Entities.Select(IdOf).ToList());
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
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // One row past the cap is all it takes to know the match set exceeds it.
            var records = await query.Take(BroadcastMaterializationCap + 1).ToListAsync(ct);
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
                await context.SaveChangesAsync(ct);
            }

            var count = await SoftDeleteRowsAsync(query, auditContext, ct);

            if (collapsed)
                await WriteBulkDeleteSummaryAsync<T>(context, count, scope, auditContext, ct);

            await transaction.CommitAsync(ct);
            return new AuditedSoftDeleteResult<T>(count, records);
        });
    }

    /// <summary>
    /// Stamps <c>DeletedAt</c> and the dedup attribution flag in one update: a user-initiated delete
    /// blocks resync re-creation, a system sweep leaves the row re-creatable
    /// (<see cref="SoftDeleteDedupExtensions"/>). Runs whether or not an audit row is written.
    /// </summary>
    private static Task<int> SoftDeleteRowsAsync<T>(
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct) where T : class, ISoftDeletable
    {
        var now = DateTime.UtcNow;
        var isUserDelete = !auditContext.IsSystemMutation();

        return query.ExecuteUpdateAsync(
            s => s
                .SetProperty(e => e.DeletedAt, now)
                .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete), ct);
    }

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
