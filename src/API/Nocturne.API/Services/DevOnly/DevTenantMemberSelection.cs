using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.DevOnly;

/// <summary>
/// Shared member-picking rules for the dev-only endpoints: which memberships
/// count as usable identities, and which one to act on when the caller didn't
/// name one.
/// </summary>
public static class DevTenantMemberSelection
{
    /// <summary>Active, non-system member subjects.</summary>
    public static List<TenantMemberRecord> Candidates(IEnumerable<TenantMemberRecord> members) =>
        members
            .Where(m => m is { SubjectIsActive: true, IsSystemSubject: false })
            .ToList();

    /// <summary>The tenant's longest-standing owner, else the first candidate.</summary>
    public static TenantMemberRecord PickOwnerOrFirst(List<TenantMemberRecord> candidates) =>
        candidates.Where(m => m.IsOwner).OrderBy(m => m.SysCreatedAt).ThenBy(m => m.Id).FirstOrDefault()
        ?? candidates[0];
}
