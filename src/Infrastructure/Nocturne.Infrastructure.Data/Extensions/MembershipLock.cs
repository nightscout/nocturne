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
/// a team's deletion takes its manager lock and then the team access lock for each site. Account
/// deletion takes the owners locks of an active subject's sites, then the manager locks of its teams,
/// then the team access locks of every site the teams it will remove have access to, then its
/// memberships' locks. No path waits for a team access lock while it holds a membership lock, so no
/// two of them can each hold what the other waits for.
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
