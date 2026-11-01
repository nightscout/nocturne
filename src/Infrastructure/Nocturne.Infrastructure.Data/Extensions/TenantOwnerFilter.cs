using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the people who own this tenant".
/// </summary>
/// <remarks>
/// Holding the owner role is not enough on its own. A deactivated subject cannot sign in, and the
/// Public subject a share link runs as is a system row with no person behind it — a caller that
/// reaches for "the owner" to notify them, or to read a setting of theirs, means neither of those.
/// Ordered by join time so a tenant with several owners resolves the same one on every call.
/// </remarks>
public static class TenantOwnerFilter
{
    /// <summary>The tenant's memberships held by a person who can sign in: not a system subject, not deactivated.</summary>
    public static IQueryable<TenantMemberEntity> PeopleOf(
        this IQueryable<TenantMemberEntity> members, Guid tenantId) =>
        members.Where(m => m.TenantId == tenantId
            && !m.Subject!.IsSystemSubject
            && m.Subject.IsActive);

    /// <summary>The tenant's owning memberships, longest-standing first.</summary>
    public static IQueryable<TenantMemberEntity> OwnersOf(
        this IQueryable<TenantMemberEntity> members, Guid tenantId) =>
        members
            .PeopleOf(tenantId)
            .Where(m => m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner))
            .InJoinOrder();

    /// <summary>Memberships ordered by when they joined, earliest first.</summary>
    public static IOrderedQueryable<TenantMemberEntity> InJoinOrder(
        this IQueryable<TenantMemberEntity> members) =>
        members
            .OrderBy(m => m.SysCreatedAt)
            .ThenBy(m => m.Id);

    public const string LastOwnerRefusal = "Cannot remove the last owner of a tenant";

    /// <summary>
    /// Whether the member holds the owner role and no other active owner would remain if it stopped.
    /// </summary>
    public static async Task<bool> IsLastOwnerAsync(
        this IQueryable<TenantMemberEntity> members,
        Guid tenantId,
        Guid memberId,
        CancellationToken ct = default) =>
        await members.AnyAsync(
            m => m.Id == memberId && m.TenantId == tenantId && m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner), ct)
        && !await members.OwnersOf(tenantId).AnyAsync(m => m.Id != memberId, ct);
}
