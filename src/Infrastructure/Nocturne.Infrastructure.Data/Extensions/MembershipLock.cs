namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A membership's lock (<see cref="AdvisoryLockExtensions"/>), keyed by tenant and subject so it covers
/// a row that does not exist yet: what every write of a membership holds from before it reads the
/// member's reasons to its commit, so two writers cannot each project from reasons the other is changing.
/// </summary>
/// <remarks>
/// Every write that takes more than one of these locks takes them in one order: the site's owners lock
/// (<see cref="TenantOwnerLock"/>), then a team's manager lock (<see cref="TeamManagerLock"/>), then
/// sites' team access locks (<see cref="TeamAccessLock"/>) in ascending tenant id, then membership locks,
/// several at once through <see cref="LockMembershipsAsync"/>. An approval takes the first three, so the
/// approver's owner role, end and limit hold still from its check to its commit; a role edit takes the
/// owners lock and the team access lock, and a role deletion then the locks of the members who hold it;
/// a site's stop of a team's access, the team's deletion and a staff member's removal take the team's
/// manager lock, then the team access lock for each site they touch, then the locks of the memberships
/// whose team reasons they remove. Account deletion takes the owners locks of an active subject's
/// sites, then the manager locks of its teams, then the team access locks of every site the teams it
/// will remove have access to, then its memberships' locks; removing every membership of a subject
/// takes the owners locks and then the manager locks, so both read the subject's memberships after a
/// team write that removes one of their reasons commits. No path waits for a team access lock while it
/// holds a membership lock, so no two of them can each hold what the other waits for.
/// <para>
/// Account deletion locks the subject's row <c>FOR UPDATE</c> before it takes membership locks
/// (<see cref="TenantOwnerLock.LockTenantsOfSubjectAsync"/>). A write that held a membership lock and
/// then inserted a row naming the subject would wait for that row <c>FOR KEY SHARE</c>, through the
/// row's foreign key, while the deletion waited for the membership lock. A consent avoids the cycle by
/// taking the subject row <c>FOR KEY SHARE</c> before its membership lock
/// (<see cref="Nocturne.Core.Contracts.Auth.IOAuthGrantRepository.LockConsentAsync"/>): its own lock on
/// the subject and client, then the subject row, then one membership lock. Nothing waits for a consent
/// lock while it holds a membership lock. The team writes that remove team reasons insert no row
/// naming the subject, so they take no subject row lock: one taken after their manager lock would
/// wait on account deletion, which waits on that manager lock.
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
