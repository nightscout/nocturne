using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the people who own this tenant".
/// </summary>
/// <remarks>
/// Holding the owner role is not enough on its own. A revoked membership has been removed from the
/// tenant, a deactivated subject cannot sign in, and the Public subject a share link runs as is a
/// system row with no person behind it — a caller that reaches for "the owner" to notify them, or
/// to read a setting of theirs, means none of those. Ordered by join time so a tenant with several
/// owners resolves the same one on every call.
/// </remarks>
public static class TenantOwnerFilter
{
    /// <summary>The tenant's owning memberships, longest-standing first.</summary>
    public static IQueryable<TenantMemberEntity> OwnersOf(
        this IQueryable<TenantMemberEntity> members, Guid tenantId) =>
        members
            .Where(m => m.TenantId == tenantId
                && m.RevokedAt == null
                && !m.Subject!.IsSystemSubject
                && m.Subject.IsActive
                && m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner))
            .OrderBy(m => m.SysCreatedAt)
            .ThenBy(m => m.Id);
}
