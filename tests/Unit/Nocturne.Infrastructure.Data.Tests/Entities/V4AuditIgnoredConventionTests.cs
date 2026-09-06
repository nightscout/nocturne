using System.Reflection;

namespace Nocturne.Infrastructure.Data.Tests.Entities;

/// <summary>
/// Convention guard: bookkeeping columns on V4 auditable entities must carry
/// <see cref="AuditIgnoredAttribute"/>. The connector pipeline rewrites
/// <c>CorrelationId</c> (a fresh per-decomposition-run GUID) and the system
/// timestamps on every re-upsert of unchanged data; without the attribute each
/// re-sync produces a mutation_audit_log "update" row whose only diff is that
/// bookkeeping — ~1.5M no-op rows/day in production. The attribute also feeds
/// V4MaterialChange, so a bookkeeping-only rewrite must not broadcast an update.
/// </summary>
public class V4AuditIgnoredConventionTests
{
    private static readonly string[] BookkeepingProperties =
    [
        "CorrelationId",
        "SysCreatedAt",
        "SysUpdatedAt",
        "DeletedAt",
    ];

    public static TheoryData<Type> V4AuditableEntityTypes()
    {
        var data = new TheoryData<Type>();
        var types = typeof(NocturneDbContext).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                        && t.Namespace == "Nocturne.Infrastructure.Data.Entities.V4"
                        && typeof(IAuditable).IsAssignableFrom(t));

        foreach (var type in types)
            data.Add(type);

        return data;
    }

    [Theory]
    [MemberData(nameof(V4AuditableEntityTypes))]
    public void BookkeepingProperties_AreAuditIgnored(Type entityType)
    {
        var unannotated = BookkeepingProperties
            .Select(name => entityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is not null && p.GetCustomAttribute<AuditIgnoredAttribute>() is null)
            .Select(p => p!.Name)
            .ToList();

        unannotated.Should().BeEmpty(
            $"{entityType.Name} bookkeeping columns are rewritten on every connector re-sync " +
            "and must be [AuditIgnored] so unchanged re-upserts do not produce audit rows or broadcasts");
    }
}
