namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The lock on the owners' notices about one team's staff at one site (<see cref="AdvisoryLockExtensions"/>):
/// what a recount holds from its read of the staff who wait to the last notice it files, so the last recount
/// to take it reads every write that committed before it and files that count.
/// </summary>
/// <remarks>
/// It is taken on a unit pinned to the site after the write it recounts has committed, and that unit takes no
/// other lock, so it is outside the order <see cref="MembershipLock"/> states. The notices are filed on other
/// connections, under the notification store's own lock, which is taken alone.
/// </remarks>
internal static class TeamStaffNoticeLock
{
    internal const int Class = 0x4E54_534E;

    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static Task LockTeamStaffNoticesAsync(this NocturneDbContext db, Guid tenantId, Guid teamId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, tenantId, teamId, ct);
}
