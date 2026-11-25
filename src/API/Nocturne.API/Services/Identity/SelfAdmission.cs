namespace Nocturne.API.Services.Identity;

/// <summary>
/// The refusals of a person who holds access only through a team letting themselves in directly:
/// accepting an invite they made, or approving their own membership request. Either would give them
/// a direct reason that outlives the team's access. Each is checked in the transaction that would
/// add the direct reason, after an ended direct reason is replaced, so it meets only a person who
/// still holds a membership with no direct reason; a member let in directly is refused as already a
/// member, and a person whose only reason has ended rejoins as before.
/// </summary>
public static class SelfAdmission
{
    /// <summary>The problem code of accepting an invite the accepting person made.</summary>
    public const string OwnInviteCode = "member_invite_own";

    /// <summary>The fallback text for a client that does not know <see cref="OwnInviteCode"/>.</summary>
    public const string OwnInviteDetail = "You made this invite, so you cannot accept it. Ask another member to invite you.";

    /// <summary>The problem code of approving a membership request the approver made.</summary>
    public const string OwnRequestCode = "membership_request_own";

    /// <summary>The fallback text for a client that does not know <see cref="OwnRequestCode"/>.</summary>
    public const string OwnRequestDetail = "You cannot approve your own request. Ask another member to approve it.";
}
