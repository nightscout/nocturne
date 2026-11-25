using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// The refusal of a member management action for a member who holds no direct reason
/// (<see cref="OwnerGuardedChange.FromTeam"/>): what they hold comes from a team's access to the
/// tenant, and only stopping that access changes it. A caller who holds no direct reason is refused
/// with <see cref="CallerAccessFromTeam"/> instead.
/// </summary>
public static class MemberAccessFromTeam
{
    /// <summary>The problem code of the refusal.</summary>
    public const string Code = "member_access_from_team";

    /// <summary>The fallback text for a client that does not know <see cref="Code"/>.</summary>
    public const string Detail =
        "This member has access only through a team. To change it, stop the team's access to this site.";
}
