using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// SaveChanges interceptor that captures mutation diffs for auditable entities
/// and appends <see cref="MutationAuditLogEntity"/> records to the same save
/// transaction. Registered as a singleton; the actor is the <see cref="NocturneDbContext.AuditContext"/>
/// of the context being saved.
/// </summary>
public class MutationAuditInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName), AuditPropertyBehavior>
        PropertyBehaviorCache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not NocturneDbContext context)
            return new ValueTask<InterceptionResult<int>>(result);

        // The DI scope that created the context supplies its actor (TenantReadContext), so a save
        // made in a child scope is attributed by that scope rather than by an enclosing request. A
        // context with no scope has none.
        var auditContext = context.AuditContext;

        var auditEntries = new List<MutationAuditLogEntity>();
        var now = time.GetUtcNow().UtcDateTime;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            // Maintain the dedup flag carried by every soft-deletable row, audited or not: a
            // user-initiated soft-delete blocks connector resync from re-creating the row; a system
            // sweep or a restore does not. A hard delete removes the row, so only soft-delete
            // transitions (Modified state) are read.
            if (entry.State == EntityState.Modified && entry.Entity is ISoftDeletable)
            {
                var deletedAt = entry.Property(nameof(ISoftDeletable.DeletedAt));
                var wasDeleted = deletedAt.OriginalValue is not null;
                var isDeleted = deletedAt.CurrentValue is not null;
                if (!wasDeleted && isDeleted)
                    entry.Property("DeletedByUser").CurrentValue = !auditContext.IsSystemMutation();
                else if (wasDeleted && !isDeleted)
                    entry.Property("DeletedByUser").CurrentValue = false;
            }

            if (entry.Entity is not IAuditable)
                continue;

            var (action, changesJson) = DetermineActionAndChanges(entry);

            // Skip if Modified state but no actual field changes
            if (action == "update" && changesJson is null)
                continue;

            // System/connector/background mutations have no human actor. They are high-volume
            // automated data ingestion (CGM readings, temp basals, etc.) whose provenance is
            // already captured on the records themselves (data_source), so they are not recorded
            // here — auditing them grew this table to 36GB / ~1.8M rows-per-day in production.
            // The soft-delete/dedup maintenance above still runs for these mutations.
            if (auditContext.IsSystemMutation())
                continue;

            auditEntries.Add(MutationAuditLogEntity.Create(
                context.TenantId,
                auditContext,
                entry.Entity.GetType().Name.Replace("Entity", ""),
                GetEntityId(entry),
                action,
                changesJson,
                now));
        }

        if (auditEntries.Count > 0)
            context.Set<MutationAuditLogEntity>().AddRange(auditEntries);

        return new ValueTask<InterceptionResult<int>>(result);
    }

    private static (string Action, string? ChangesJson) DetermineActionAndChanges(EntityEntry entry)
    {
        return entry.State switch
        {
            EntityState.Added => ("create", null),
            EntityState.Deleted => ("delete", BuildDeleteSnapshot(entry)),
            EntityState.Modified => DetermineModifiedAction(entry),
            _ => throw new InvalidOperationException($"Unexpected entity state: {entry.State}")
        };
    }

    private static (string Action, string? ChangesJson) DetermineModifiedAction(EntityEntry entry)
    {
        // Detect soft delete: ISoftDeletable.DeletedAt changed null -> non-null
        if (entry.Entity is ISoftDeletable)
        {
            var deletedAtProp = entry.Property(nameof(ISoftDeletable.DeletedAt));
            var oldValue = deletedAtProp.OriginalValue as DateTime?;
            var newValue = deletedAtProp.CurrentValue as DateTime?;

            if (oldValue is null && newValue is not null)
                return ("delete", BuildDeleteSnapshot(entry));

            if (oldValue is not null && newValue is null)
                return ("restore", BuildRestoreSnapshot(entry));
        }

        var changes = BuildUpdateChanges(entry);
        return ("update", changes);
    }

    private static string? BuildUpdateChanges(EntityEntry entry)
    {
        var changes = new Dictionary<string, object>();

        foreach (var prop in entry.Properties)
        {
            if (prop.Metadata.IsPrimaryKey())
                continue;

            var behavior = GetPropertyBehavior(entry.Entity.GetType(), prop.Metadata.Name);

            if (behavior == AuditPropertyBehavior.Ignored)
                continue;

            if (!prop.IsModified)
                continue;

            var oldValue = prop.OriginalValue;
            var newValue = prop.CurrentValue;

            if (Equals(oldValue, newValue))
                continue;

            if (behavior == AuditPropertyBehavior.Redacted)
            {
                changes[prop.Metadata.Name] = new Dictionary<string, object?>
                {
                    ["old"] = "[redacted]",
                    ["new"] = "[redacted]"
                };
            }
            else
            {
                changes[prop.Metadata.Name] = new Dictionary<string, object?>
                {
                    ["old"] = oldValue,
                    ["new"] = newValue
                };
            }
        }

        if (changes.Count == 0)
            return null;

        return JsonSerializer.Serialize(changes, JsonOptions);
    }

    private static string BuildDeleteSnapshot(EntityEntry entry)
    {
        var snapshot = new Dictionary<string, object?>();

        foreach (var prop in entry.Properties)
        {
            if (prop.Metadata.IsPrimaryKey())
                continue;

            var behavior = GetPropertyBehavior(entry.Entity.GetType(), prop.Metadata.Name);

            if (behavior == AuditPropertyBehavior.Ignored)
                continue;

            snapshot[prop.Metadata.Name] = behavior == AuditPropertyBehavior.Redacted
                ? "[redacted]"
                : prop.OriginalValue;
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static string BuildRestoreSnapshot(EntityEntry entry)
    {
        var snapshot = new Dictionary<string, object?>();

        foreach (var prop in entry.Properties)
        {
            if (prop.Metadata.IsPrimaryKey())
                continue;

            var behavior = GetPropertyBehavior(entry.Entity.GetType(), prop.Metadata.Name);

            if (behavior == AuditPropertyBehavior.Ignored)
                continue;

            snapshot[prop.Metadata.Name] = behavior == AuditPropertyBehavior.Redacted
                ? "[redacted]"
                : prop.CurrentValue;
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static Guid GetEntityId(EntityEntry entry)
    {
        var idProp = entry.Property("Id");
        return (Guid)idProp.CurrentValue!;
    }

    private static AuditPropertyBehavior GetPropertyBehavior(Type entityType, string propertyName)
    {
        return PropertyBehaviorCache.GetOrAdd((entityType, propertyName), key =>
        {
            var property = key.EntityType.GetProperty(key.PropertyName,
                BindingFlags.Public | BindingFlags.Instance);

            if (property is null)
                return AuditPropertyBehavior.Normal;

            if (property.GetCustomAttribute<AuditIgnoredAttribute>() is not null)
                return AuditPropertyBehavior.Ignored;

            if (property.GetCustomAttribute<AuditRedactedAttribute>() is not null)
                return AuditPropertyBehavior.Redacted;

            return AuditPropertyBehavior.Normal;
        });
    }

    private enum AuditPropertyBehavior
    {
        Normal,
        Ignored,
        Redacted
    }
}
