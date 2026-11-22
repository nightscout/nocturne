using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// How the dev-only snapshot import removes and restores memberships: through
/// <see cref="MembershipReasons"/>, so a restored member has its direct reason.
/// </summary>
/// <remarks>
/// It takes no owners lock, checks no last owner and writes no audit row but the joined and removed
/// rows <see cref="MembershipReasons"/> writes for every membership, so only the dev snapshot import
/// may call it; <c>MembershipWriteGuardTests</c> pins that.
/// </remarks>
public static class DevSnapshotMemberships
{
    /// <summary>Removes every membership of <paramref name="tenantId"/> with its reasons and role links.</summary>
    public static async Task RemoveMembersOfTenantAsync(NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var members = await db.TenantMembers.Where(m => m.TenantId == tenantId).ToListAsync(ct);
        foreach (var member in members)
            await MembershipReasons.RemoveMemberAsync(db, member, MembershipEndHow.System, ct);
    }

    /// <summary>
    /// Restores <paramref name="member"/>, which carries only its row-only state, with a direct reason
    /// that gives <paramref name="direct"/>.
    /// </summary>
    public static void Restore(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, Guid? createdFromInviteId) =>
        MembershipReasons.AddMember(
            db, member, direct, createdFromInviteId, MembershipJoinHow.System, by: null, source: null);
}
