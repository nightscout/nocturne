namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A membership's lock (<see cref="AdvisoryLockExtensions"/>), keyed by tenant and subject so it covers
/// a row that does not exist yet: what every write of a membership holds from before it reads the
/// member's reasons to its commit, so two writers cannot each project from reasons the other is changing.
/// </summary>
/// <remarks>
/// <para>
/// Every write that takes more than one of these locks takes them in one order: the site's owners lock
/// (<see cref="TenantOwnerLock"/>), then a team's manager lock (<see cref="TeamManagerLock"/>), then
/// sites' team access locks (<see cref="TeamAccessLock"/>) in ascending tenant id, then membership locks,
/// several at once through <see cref="LockMembershipsAsync"/>. No path waits for one of them while it
/// holds one that comes later, so no two of them can each hold what the other waits for.
/// </para>
/// <para>
/// Every write that may insert a row naming a subject it did not create takes that subject's row
/// <c>FOR KEY SHARE</c> before any of these locks (<see cref="TenantOwnerLock.LockSubjectRowsSharedAsync"/>,
/// in ascending id). Account deletion takes the row <c>FOR UPDATE</c> before any lock, whether or not the
/// subject is active (<see cref="TenantOwnerLock.LockSubjectRowAsync"/>), and then waits for these locks,
/// so a write that held one of them and then inserted a row naming the subject would wait for the
/// deletion through the row's foreign key while the deletion waited for it. Adding a member inserts the
/// membership, and so can a team reason (<see cref="Services.TeamReasons"/>). A write that learns under its
/// locks of a subject it did not lock first gives up before writing and runs again.
/// </para>
/// <para>
/// The writes, each in that order: an approval of a team's access takes the subject rows of the approver
/// and the team's staff, the owners lock, the manager lock and the team access lock, so the approver's
/// owner role, end and limit hold still from its check to its commit. An owner's edit of a team's access and
/// the switch of who decides its staff take the same locks without the owner's row, since they insert no row
/// naming the owner; an owner's decision on one staff member takes that member's row, the owners lock and the
/// team access lock. A role edit takes the rows of the
/// staff it may sync, the owners lock and the team access lock; a role deletion takes the owners lock, the
/// team access lock, then the locks of the members who hold it. A staff member joining or being edited
/// takes their row, the team's manager lock and the team access lock of every site the team follows. A
/// site's stop of a team's access, the team's deletion and a staff member's removal take the team's
/// manager lock and the team access lock of each site they touch; they only remove reasons and insert no
/// row naming a subject. The startup sync takes one subject's row, then one site's team access lock. A
/// write that can change whether a subject owns a site (a role change, a step-down, a removal, an access
/// end, an invite accepted, a request or an access request approved, a member added by the platform)
/// takes the subject's row, the owners lock and the team access lock, then syncs the subject's team
/// reasons there. Adding a member takes the subject's row before the membership lock, and a caller
/// that takes one of these locks before the add takes the row first; setup's adoption of a provider
/// holder takes the holder's row before the setup lock and its two membership locks. A consent takes its own lock on the
/// subject and client, then the subject row, then one membership lock
/// (<see cref="Nocturne.Core.Contracts.Auth.IOAuthGrantRepository.LockConsentAsync"/>). Each of these takes
/// the locks of the memberships it changes last. Account deletion takes the subject's row, then the owners
/// locks of an active subject's sites, then the manager locks of its teams, then the team access locks of every site the
/// teams it will remove have access to, then its memberships' locks; removing every membership of a
/// subject takes the row, the owners locks and then the manager locks, so both read the subject's
/// memberships after a team write that removes one of their reasons commits.
/// </para>
/// </remarks>
internal static class MembershipLock
{
    internal const int Class = 0x4E4D_4252;

    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static Task LockMembershipAsync(this NocturneDbContext db, Guid tenantId, Guid subjectId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, tenantId, subjectId, ct);

    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static Task LockMembershipsAsync(
        this NocturneDbContext db, IEnumerable<(Guid TenantId, Guid SubjectId)> memberships, CancellationToken ct) =>
        db.LockAllForTransactionAsync(Class, memberships, ct);
}
