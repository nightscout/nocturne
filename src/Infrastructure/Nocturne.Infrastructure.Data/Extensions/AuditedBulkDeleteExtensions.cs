using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Extensions for executing bulk deletes that write per-row audit log entries before removal.
/// </summary>
public static class AuditedBulkDeleteExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Executes a bulk delete with audit trail. Queries affected records,
    /// writes audit entries, then executes the delete — all in one transaction.
    /// </summary>
    public static async Task<int> AuditedExecuteDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct = default) where T : class, IAuditable
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Query affected records for audit snapshot
            var affectedRecords = await query.ToListAsync(ct);

            if (affectedRecords.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            var now = DateTime.UtcNow;
            var entityTypeName = typeof(T).Name.Replace("Entity", "");

            var auditEntries = affectedRecords.Select(record =>
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
                    AuthType = auditContext?.AuthType,
                    IpAddress = auditContext?.IpAddress,
                    TokenId = auditContext?.TokenId,
                    CorrelationId = auditContext?.CorrelationId,
                    Endpoint = auditContext?.Endpoint,
                    CreatedAt = now
                };
            }).ToList();

            // Detach the loaded entities so they don't interfere with the bulk delete
            foreach (var record in affectedRecords)
                context.Entry(record).State = EntityState.Detached;

            // Write audit entries
            context.Set<MutationAuditLogEntity>().AddRange(auditEntries);
            await context.SaveChangesAsync(ct);

            // Execute bulk delete
            var deletedCount = await query.ExecuteDeleteAsync(ct);

            await transaction.CommitAsync(ct);
            return deletedCount;
        });
    }

    /// <summary>
    /// Executes a bulk soft delete with audit trail. Queries affected records,
    /// writes audit entries, then sets <c>DeletedAt</c> — all in one transaction.
    /// </summary>
    /// <returns>The number of records soft-deleted.</returns>
    public static async Task<int> AuditedSoftDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
        => (await context.AuditedSoftDeleteWithEntitiesAsync(query, auditContext, ct)).Count;

    /// <summary>
    /// As <see cref="AuditedSoftDeleteAsync{T}"/>, but returns the ids of the soft-deleted records so the
    /// caller can broadcast per-record delete events. The records are already materialized for the audit
    /// snapshot, so surfacing their ids costs no extra query.
    /// </summary>
    public static async Task<List<Guid>> AuditedSoftDeleteWithIdsAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
        => (await context.AuditedSoftDeleteWithEntitiesAsync(query, auditContext, ct))
            .Select(e => (Guid)typeof(T).GetProperty("Id")!.GetValue(e)!)
            .ToList();

    /// <summary>
    /// As <see cref="AuditedSoftDeleteAsync{T}"/>, but returns the soft-deleted entities so the caller can
    /// project them (e.g. to the legacy <c>Entry</c> shape) and broadcast per-record delete events. The
    /// records are already materialized for the audit snapshot, so surfacing them costs no extra query.
    /// They are detached after the snapshot but still hold their loaded values.
    /// </summary>
    public static async Task<List<T>> AuditedSoftDeleteWithEntitiesAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        IAuditContext? auditContext,
        CancellationToken ct = default) where T : class, IAuditable, ISoftDeletable
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Query affected records for audit snapshot
            var affectedRecords = await query.ToListAsync(ct);

            if (affectedRecords.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return [];
            }

            var now = DateTime.UtcNow;
            var entityTypeName = typeof(T).Name.Replace("Entity", "");

            var auditEntries = affectedRecords.Select(record =>
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
                    AuthType = auditContext?.AuthType,
                    IpAddress = auditContext?.IpAddress,
                    TokenId = auditContext?.TokenId,
                    CorrelationId = auditContext?.CorrelationId,
                    Endpoint = auditContext?.Endpoint,
                    CreatedAt = now
                };
            }).ToList();

            // Detach the loaded entities so they don't interfere with the bulk update
            foreach (var record in affectedRecords)
                context.Entry(record).State = EntityState.Detached;

            // Write audit entries
            context.Set<MutationAuditLogEntity>().AddRange(auditEntries);
            await context.SaveChangesAsync(ct);

            // Execute bulk soft delete, carrying the dedup attribution flag in the same
            // update: a user-initiated delete blocks resync re-creation, a system sweep
            // (no auth context) leaves the row re-creatable.
            var isUserDelete = auditContext?.AuthType != null;
            await query.ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.DeletedAt, now)
                    .SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), isUserDelete), ct);

            await transaction.CommitAsync(ct);
            return affectedRecords;
        });
    }

    /// <summary>
    /// Executes a bulk soft delete without audit trail by setting <c>DeletedAt</c> on all matching rows.
    /// </summary>
    public static async Task<int> SoftDeleteAsync<T>(
        this NocturneDbContext context,
        IQueryable<T> query,
        CancellationToken ct = default) where T : class, ISoftDeletable
    {
        return await query.ExecuteUpdateAsync(
            s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }
}
