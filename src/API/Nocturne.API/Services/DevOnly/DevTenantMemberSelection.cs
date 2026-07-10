using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.DevOnly;

/// <summary>
/// Shared member-picking rules for the dev-only endpoints: which memberships
/// count as usable identities, and which one to act on when the caller didn't
/// name one. Callers must have loaded Subject and MemberRoles.TenantRole
/// navigations.
/// </summary>
public static class DevTenantMemberSelection
{
    /// <summary>Active, non-system member subjects.</summary>
    public static List<TenantMemberEntity> Candidates(IEnumerable<TenantMemberEntity> members) =>
        members
            .Where(m => m.Subject is { IsActive: true, IsSystemSubject: false })
            .ToList();

    /// <summary>The first owner-role member, else the first candidate.</summary>
    public static TenantMemberEntity PickOwnerOrFirst(List<TenantMemberEntity> candidates) =>
        candidates.FirstOrDefault(m => m.MemberRoles.Any(mr =>
            mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner))
        ?? candidates[0];
}
