using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
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
    /// <summary>The tenant's memberships held by a person: active and not a system subject.</summary>
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

    public const string LastOwnerRefusal = "Cannot remove the only owner, or the last owner who can sign in";

    /// <summary>
    /// Whether the member holds the owner role and stopping would leave the tenant with no active
    /// owner, or, when the member can sign in (<see cref="OrphanedSubjectFilter"/>), with no other
    /// owner who can.
    /// </summary>
    /// <remarks>
    /// An owner holds <see cref="Scope.FullAccess"/>, so an owner who can sign in is a member
    /// administrator who can, and while one remains the site is not in recovery mode
    /// (<see cref="Core.Models.Identity.SetupGate.RecoveryMode"/>). The guard keeps the last of them
    /// and the last owner of any kind. An owner who cannot sign in may go while another owner
    /// remains: that removes no way in.
    /// </remarks>
    /// <param name="db">The context the caller holds the owner lock on (<see cref="TenantOwnerLock"/>).</param>
    /// <param name="tenantId">The tenant whose owners are counted.</param>
    /// <param name="memberId">The membership that would stop being an owner.</param>
    /// <param name="linkRoute">Decides who cannot sign in.</param>
    /// <param name="ct">Cancels the read.</param>
    public static async Task<bool> IsLastOwnerAsync(
        this NocturneDbContext db,
        Guid tenantId,
        Guid memberId,
        IAccountLinkRoute linkRoute,
        CancellationToken ct = default)
    {
        var stuck = db.OrphanedSubjectsOf(tenantId, linkRoute);
        var member = await db.TenantMembers.AsNoTracking()
            .Where(m => m.Id == memberId && m.TenantId == tenantId && m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner))
            .Select(m => new { CanSignIn = !stuck.Any(s => s.Id == m.SubjectId) })
            .FirstOrDefaultAsync(ct);
        if (member is null)
            return false;

        var otherOwners = db.TenantMembers.AsNoTracking().OwnersOf(tenantId).Where(m => m.Id != memberId);
        if (!await otherOwners.AnyAsync(ct))
            return true;
        return member.CanSignIn && !await otherOwners.AnyAsync(m => !stuck.Any(s => s.Id == m.SubjectId), ct);
    }
}
