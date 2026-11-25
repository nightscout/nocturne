namespace Nocturne.Core.Models.Identity;

/// <summary>The access one reason gives a subject on a tenant, or the sum of several.</summary>
/// <param name="AccessEndsAt">When the access ends, in UTC; null when it does not.</param>
public sealed record MembershipGrant(
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt);

/// <summary>What a membership's row stores at an instant: the sum of its reasons then, and when the next of them lapses.</summary>
/// <param name="Grant">The access the row gives.</param>
/// <param name="NextReasonLapseAt">
/// The earliest end of a reason in force that is earlier than <see cref="MembershipGrant.AccessEndsAt"/>
/// of <paramref name="Grant"/>, or any such end when it has none; null when no reason ends before the row does.
/// </param>
public sealed record MembershipProjection(MembershipGrant Grant, DateTime? NextReasonLapseAt);

/// <summary>How a membership's reasons combine into the access its row stores.</summary>
/// <remarks>
/// <para>
/// The reasons summed are the ones in force: a reason whose own end has passed gives nothing and is
/// not passed in. <see cref="InForceAt"/> chooses them.
/// </para>
/// <para>
/// No reasons sum to null, and the membership is removed. Only the count of reasons removes it: a
/// reason with no roles and no permissions is still a reason, as the Public member is before sharing
/// is turned on.
/// </para>
/// <para>
/// One reason sums to itself exactly, down to null against empty permissions and their order, so a
/// membership with one reason stores what it always stored.
/// </para>
/// <para>
/// Several reasons sum to the union of their roles; the distinct union of their direct permissions in
/// first-seen order, null only when every reason's is null; the 24-hour limit only when every reason
/// has it; and no end when any reason has none, otherwise the latest end. Each reason only adds
/// access, so removing one never takes away what another gives.
/// </para>
/// <para>
/// The end <see cref="InForceAt"/> gives is the same at every instant: a reason with no end is always
/// in force, and the reason that ends latest is in force whenever any is, and is the latest of them
/// all when none is. So a reason lapsing never moves the end. Only adding or removing a reason, or
/// changing one's end, does, and only removing one or bringing its end forward can lower it.
/// </para>
/// </remarks>
public static class MembershipSum
{
    /// <summary>The access <paramref name="reasons"/> give together; null when there are none.</summary>
    public static MembershipGrant? Of(IReadOnlyList<MembershipGrant> reasons)
    {
        if (reasons.Count == 0)
            return null;
        if (reasons.Count == 1)
            return reasons[0];

        var permissionLists = reasons.Select(r => r.DirectPermissions).OfType<IReadOnlyList<string>>().ToList();
        return new MembershipGrant(
            reasons.SelectMany(r => r.RoleIds).Distinct().ToList(),
            permissionLists.Count == 0 ? null : permissionLists.SelectMany(p => p).Distinct().ToList(),
            reasons.All(r => r.LimitTo24Hours),
            reasons.Any(r => r.AccessEndsAt is null) ? null : reasons.Max(r => r.AccessEndsAt));
    }

    /// <summary>
    /// The row <paramref name="reasons"/> give at <paramref name="now"/>: the <see cref="Of"/> of those
    /// in force, each one whose end is null or later than <paramref name="now"/>, the rule a membership's
    /// own end follows. When none is in force it is the sum of them all, which has ended, so the row
    /// reads as ended and is kept. Null when there are no reasons.
    /// </summary>
    public static MembershipProjection? InForceAt(IReadOnlyList<MembershipGrant> reasons, DateTime now)
    {
        var inForce = reasons.Where(r => r.AccessEndsAt is not { } end || end > now).ToList();
        if (Of(inForce.Count == 0 ? reasons : inForce) is not { } sum)
            return null;

        var lapse = inForce
            .Select(r => r.AccessEndsAt)
            .Where(end => end is { } e && (sum.AccessEndsAt is not { } rowEnd || e < rowEnd))
            .Min();
        return new MembershipProjection(sum, lapse);
    }
}

/// <summary>The kinds of reason a subject holds a membership for.</summary>
public static class MembershipReasonKind
{
    /// <summary>
    /// Access granted on the tenant itself: by its owner, an invite, an approval or setup, or by the
    /// system for the Public and device subjects.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// Access a team's access to the tenant gives a member of the team. Its source is that access
    /// (<c>team_tenant_access</c>), which cannot be deleted while a reason names it, and it never gives
    /// the owner role.
    /// </summary>
    public const string Team = "team";
}
