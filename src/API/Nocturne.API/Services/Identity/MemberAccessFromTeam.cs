using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// The refusal of a member management action for a member who holds no direct reason
/// (<see cref="OwnerGuardedChange.FromTeam"/>): what they hold comes from a team's access to the
/// tenant, and only stopping that access changes it. The same code refuses a caller who holds no
/// direct reason anything they would give someone else (<see cref="Extensions.HttpContextExtensions.CallerGrantCeiling"/>).
/// </summary>
public static class MemberAccessFromTeam
{
    /// <summary>The problem code of the refusal.</summary>
    public const string Code = "member_access_from_team";

    /// <summary>The fallback text for a client that does not know <see cref="Code"/>.</summary>
    public const string Detail =
        "This member has access only through a team. To change it, stop the team's access to this site.";

    /// <summary>The fallback text when the caller is the one whose access comes only from a team.</summary>
    public const string CallerDetail =
        "You have access to this site only through a team, so you cannot give access to anyone.";
}
