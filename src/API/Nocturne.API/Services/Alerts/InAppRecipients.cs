using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Who an <c>in_app</c> alert channel may name: a person who is a member of the rule's tenant
/// (<see cref="TenantOwnerFilter.PeopleOf"/>). A notification is listed only to the subject it
/// names and only within its own tenant, so a channel naming anyone else reaches no one.
/// </summary>
internal static class InAppRecipients
{
    /// <summary>The subject <paramref name="destination"/> names, when it may receive; otherwise null.</summary>
    public static async Task<Guid?> ResolveAsync(
        NocturneDbContext db, Guid tenantId, string? destination, CancellationToken ct)
    {
        if (!Guid.TryParse(destination, out var subjectId))
        {
            return null;
        }

        return await db.TenantMembers.PeopleOf(tenantId).AnyAsync(m => m.SubjectId == subjectId, ct)
            ? subjectId
            : null;
    }

    /// <summary>Every subject that may receive, with the name the site knows them by.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ListAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var members = await db.TenantMembers.PeopleOf(tenantId)
            .OrderBy(m => m.SysCreatedAt)
            .Select(m => new { m.SubjectId, Name = m.Label ?? m.Subject!.Name })
            .ToListAsync(ct);

        var names = new Dictionary<Guid, string>();
        foreach (var member in members)
            names.TryAdd(member.SubjectId, member.Name);
        return names;
    }

    /// <summary>The tenant's longest-standing owner, or null when it has none.</summary>
    public static async Task<Guid?> EarliestOwnerAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.TenantMembers.OwnersOf(tenantId)
            .Select(m => (Guid?)m.SubjectId)
            .FirstOrDefaultAsync(ct);
}
