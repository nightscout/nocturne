using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Decides whether an upserted-in-place entity changed materially — i.e. worth broadcasting as an
/// <c>update</c>. Uses the SAME predicate the <c>MutationAuditInterceptor</c> applies when deciding
/// whether to write an audit row: some non-primary-key, non-<see cref="AuditIgnoredAttribute"/>
/// property is modified. This keeps the invariant "we broadcast an update iff we audited a change", so
/// connector re-polls that re-send byte-identical rows (no modified property) broadcast nothing, while
/// genuine value/timestamp re-corrections do.
/// </summary>
internal static class V4MaterialChange
{
    private static readonly ConcurrentDictionary<(Type, string), bool> AuditIgnoredCache = new();

    /// <summary>True if the tracked entity has at least one material (non-PK, non-[AuditIgnored]) modified property.</summary>
    public static bool HasMaterialChange(EntityEntry entry)
    {
        var entityType = entry.Entity.GetType();
        return entry.Properties.Any(prop =>
            prop.IsModified
            && !prop.Metadata.IsPrimaryKey()
            && !IsAuditIgnored(entityType, prop.Metadata.Name));
    }

    private static bool IsAuditIgnored(Type entityType, string propertyName)
        => AuditIgnoredCache.GetOrAdd((entityType, propertyName), key =>
            key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance)
                ?.GetCustomAttribute<AuditIgnoredAttribute>() is not null);
}
