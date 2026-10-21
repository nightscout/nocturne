using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Who an <c>in_app</c> alert channel may name: a person who is a member of the rule's tenant
/// (<see cref="TenantOwnerFilter.PeopleOf"/>) and whose roles or direct permissions grant any
/// access, which a member given the denied role does not. A notification is listed only to the
/// subject it names and only within its own tenant, so a channel naming anyone else reaches no
/// one.
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

        var memberships = await Candidates(db, tenantId)
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync(ct);

        return memberships.Any(MayReceive) ? subjectId : null;
    }

    /// <summary>Every subject that may receive, with the name the site knows them by.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ListAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var memberships = await Candidates(db, tenantId)
            .OrderBy(m => m.SysCreatedAt)
            .ToListAsync(ct);

        var names = new Dictionary<Guid, string>();
        foreach (var membership in memberships.Where(MayReceive))
            names.TryAdd(membership.SubjectId, membership.Label ?? membership.Subject!.Name);
        return names;
    }

    /// <summary>The tenant's longest-standing owner who may receive, or null when it has none.</summary>
    public static async Task<Guid?> EarliestOwnerAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var owners = await db.TenantMembers.OwnersOf(tenantId)
            .AsNoTracking()
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .ToListAsync(ct);

        return owners.FirstOrDefault(MayReceive)?.SubjectId;
    }

    private static IQueryable<TenantMemberEntity> Candidates(NocturneDbContext db, Guid tenantId) =>
        db.TenantMembers.PeopleOf(tenantId)
            .AsNoTracking()
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole);

    private static bool MayReceive(TenantMemberEntity membership) =>
        membership.EffectivePermissions().Any();
}
