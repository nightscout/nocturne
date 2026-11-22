namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A site's team access lock (<see cref="AdvisoryLockExtensions"/>): what giving a team access,
/// stopping it, and editing or deleting a role hold from their check to their commit.
/// </summary>
/// <remarks>
/// An approval reads the chosen roles' permissions and a role edit reads whether a team's access gives
/// the role. Without the lock each could read before the other commits, and a team would hold a role
/// that runs the site.
/// <para>
/// Every write that takes more than one of these locks takes them in one order: the site's owners lock
/// (<see cref="TenantOwnerLock"/>), then a team's manager lock (<see cref="TeamManagerLock"/>), then
/// sites' team access locks in ascending tenant id. An approval takes all three, so the approver's
/// owner role, end and limit hold still from its check to its commit; a role edit takes the owners
/// lock and this one; a team's deletion takes its manager lock and then this one for each site.
/// Account deletion takes owners locks and then manager locks, in the same order, so no two of them
/// can each hold what the other waits for.
/// </para>
/// </remarks>
internal static class TeamAccessLock
{
    internal const int Class = 0x4E54_5441;

    internal static Task LockTeamAccessAsync(this NocturneDbContext db, Guid tenantId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, tenantId, ct);
}
