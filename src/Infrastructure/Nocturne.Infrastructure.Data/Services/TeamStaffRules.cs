using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// What may narrow a staff member of a team, and when an edit gives them more than they held.
/// </summary>
public static class TeamStaffRules
{
    /// <summary>
    /// Whether <paramref name="after"/> lets a staff member reach more than <paramref name="before"/>
    /// did: a wider ceiling, the 24-hour limit lifted, or an end that is later or removed. A change
    /// that cannot be shown to narrow every one of the three is wider.
    /// </summary>
    public static bool Widens(TeamStaffNarrowing before, TeamStaffNarrowing after) =>
        CeilingWidens(before.RoleCeilingSlug, after.RoleCeilingSlug)
        || (before.LimitTo24Hours && !after.LimitTo24Hours)
        || MembershipLiveness.Outlasts(before.EndsAt, after.EndsAt);

    /// <summary>
    /// Whether the ceiling <paramref name="after"/> is wider than <paramref name="before"/>: null is the
    /// widest, and between two slugs <paramref name="after"/> is within <paramref name="before"/> when
    /// every permission of its seed role is one <paramref name="before"/>'s seed role holds. A slug no
    /// seed role has is never within another, except itself.
    /// </summary>
    public static bool CeilingWidens(string? before, string? after)
    {
        if (before == after || before is null)
            return false;
        if (after is null)
            return true;

        return !(RoleSeeds.Permissions.TryGetValue(after, out var inner)
            && RoleSeeds.Permissions.TryGetValue(before, out var outer)
            && Scope.ValidateGrant(inner, outer) is null);
    }
}
