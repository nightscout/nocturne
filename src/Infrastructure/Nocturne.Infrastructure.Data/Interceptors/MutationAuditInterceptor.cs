using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// SaveChanges interceptor that captures mutation diffs for auditable entities
/// and appends <see cref="MutationAuditLogEntity"/> records to the same save
/// transaction. Registered as a singleton; scoped <see cref="IAuditContext"/>
/// is resolved through <see cref="IHttpContextAccessor"/>.
/// </summary>
public class MutationAuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName), AuditPropertyBehavior>
        PropertyBehaviorCache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Initializes the interceptor with access to the current HTTP context for audit metadata.
    /// </summary>
    public MutationAuditInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not NocturneDbContext context)
            return new ValueTask<InterceptionResult<int>>(result);

        // HTTP requests: resolve from the request's DI scope via HttpContext.
        // Background services: fall back to the context-level AuditContext property
        // which services populate directly when creating scopes.
        var auditContext = _httpContextAccessor.HttpContext?.RequestServices
            .GetService(typeof(IAuditContext)) as IAuditContext
            ?? context.AuditContext;

        var auditEntries = new List<MutationAuditLogEntity>();
        var now = DateTime.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var (action, changesJson) = DetermineActionAndChanges(entry);

            // Skip if Modified state but no actual field changes
            if (action == "update" && changesJson is null)
                continue;

            var audit = new MutationAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                EntityType = entry.Entity.GetType().Name.Replace("Entity", ""),
                EntityId = GetEntityId(entry),
                Action = action,
                ChangesJson = changesJson,
                SubjectId = auditContext?.SubjectId,
                SubjectName = auditContext?.SubjectName,
                AuthType = auditContext?.AuthType,
                IpAddress = auditContext?.IpAddress,
                TokenId = auditContext?.TokenId,
                CorrelationId = auditContext?.CorrelationId,
                Endpoint = auditContext?.Endpoint,
                CreatedAt = now
            };

            auditEntries.Add(audit);
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
