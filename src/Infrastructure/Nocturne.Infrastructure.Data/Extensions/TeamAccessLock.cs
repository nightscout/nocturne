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
/// It is taken in the order <see cref="MembershipLock"/> states.
/// </para>
/// </remarks>
internal static class TeamAccessLock
{
    internal const int Class = 0x4E54_5441;

    internal static Task LockTeamAccessAsync(this NocturneDbContext db, Guid tenantId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, tenantId, ct);
}
