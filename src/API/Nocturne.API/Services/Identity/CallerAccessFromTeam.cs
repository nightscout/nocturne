namespace Nocturne.API.Services.Identity;

/// <summary>
/// The refusal of anything a caller would give someone else when the caller holds no direct reason
/// (<see cref="Extensions.HttpContextExtensions.CallerGrantCeiling"/>): their own access comes from a
/// team's access to the tenant. A member who holds no direct reason, and is the target of the action,
/// is refused with <see cref="MemberAccessFromTeam"/> instead.
/// </summary>
public static class CallerAccessFromTeam
{
    /// <summary>The problem code of the refusal.</summary>
    public const string Code = "caller_access_from_team";

    /// <summary>The fallback text for a client that does not know <see cref="Code"/>.</summary>
    public const string Detail =
        "You have access to this site only through a team, so you cannot give access to anyone.";
}
