namespace Nocturne.Core.Models.Identity;

/// <summary>The access one reason gives a subject on a tenant, or the sum of several.</summary>
/// <param name="AccessEndsAt">When the access ends, in UTC; null when it does not.</param>
public sealed record MembershipGrant(
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt);

/// <summary>How a membership's reasons combine into the access its row stores.</summary>
/// <remarks>
/// <para>
/// The reasons summed are the ones in force: a reason whose own end has passed gives nothing and is
/// not passed in.
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
}

/// <summary>The kinds of reason a subject holds a membership for.</summary>
public static class MembershipReasonKind
{
    /// <summary>
    /// Access granted on the tenant itself: by its owner, an invite, an approval or setup, or by the
    /// system for the Public and device subjects.
    /// </summary>
    public const string Direct = "direct";
}
