using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

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
    /// <summary>
    /// Removes every membership of <paramref name="tenantId"/> with its reasons and role links, and
    /// saves, in a transaction under their locks (<see cref="MembershipLock"/>): the caller's, when it
    /// has one open.
    /// </summary>
    public static Task RemoveMembersOfTenantAsync(NocturneDbContext db, Guid tenantId, CancellationToken ct) =>
        db.ExecuteInTransactionAsync(async token =>
        {
            var members = await db.TenantMembers.Where(m => m.TenantId == tenantId).ToListAsync(token);
            await db.LockMembershipsAsync(members.Select(m => (m.TenantId, m.SubjectId)), token);
            foreach (var member in members)
                await MembershipReasons.RemoveMemberAsync(db, member, MembershipEndHow.System, token);
            await db.SaveChangesAsync(token);
        }, ct);

    /// <summary>
    /// Restores <paramref name="member"/>, which carries only its row-only state, with a direct reason
    /// that gives <paramref name="direct"/>. It takes no membership lock: it reads no reasons, and the
    /// import restores only into a tenant whose memberships it has just removed.
    /// </summary>
    public static void Restore(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, Guid? createdFromInviteId) =>
        MembershipReasons.AddMember(
            db, member, direct, createdFromInviteId, MembershipJoinHow.System, by: null, source: null);
}
