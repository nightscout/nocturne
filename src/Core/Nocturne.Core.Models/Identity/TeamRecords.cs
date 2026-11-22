namespace Nocturne.Core.Models.Identity;

/// <summary>What a member of a team is there as.</summary>
public static class TeamMemberKind
{
    /// <summary>Runs the team: renames it, adds and removes managers, and deletes it.</summary>
    public const string Manager = "manager";
}

/// <summary>A team the caller is a member of.</summary>
public sealed record TeamSummary(Guid Id, string Name);

/// <summary>One manager of a team.</summary>
public sealed record TeamManager(Guid SubjectId, string DisplayName);

/// <summary>
/// A manager invite that can still be accepted, and the manager who made it. The token is never read back.
/// </summary>
public sealed record TeamManagerInvite(
    Guid Id, string? Label, DateTime ExpiresAt, int? MaxUses, int UseCount, Guid CreatedBySubjectId, string CreatedByName);

/// <summary>
/// A team as one of its members reads it.
/// </summary>
/// <param name="IsLastManager">
/// Whether the caller is the manager the team cannot lose, so they cannot leave and no one can remove
/// them.
/// </param>
/// <param name="Invites">The live manager invites; empty unless the caller is a manager.</param>
/// <param name="CanDelete">Whether the caller may delete the team now.</param>
public sealed record TeamDetail(
    Guid Id,
    string Name,
    IReadOnlyList<TeamManager> Managers,
    IReadOnlyList<TeamManagerInvite> Invites,
    bool IsLastManager,
    bool CanDelete);

/// <summary>A manager invite as it is stored, before it has an id.</summary>
public sealed record NewTeamInvite(
    string TokenHash,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses);

/// <summary>Why a team invite cannot be accepted.</summary>
public enum TeamInviteRefusal
{
    NotFound,
    Revoked,
    Expired,
    UsedUp,

    /// <summary>The manager who made the invite no longer manages the team.</summary>
    NoLongerValid,

    AlreadyMember,
}

/// <summary>
/// The team a manager invite is for, read by the person holding its token.
/// </summary>
/// <param name="Refusal">Why it cannot be accepted, or null when it can.</param>
public sealed record TeamInviteInfo(Guid TeamId, string TeamName, string CreatedByName, TeamInviteRefusal? Refusal);

/// <summary>What accepting a team invite did: the team joined, or why it was refused.</summary>
public sealed record TeamInviteAcceptance(Guid? TeamId, TeamInviteRefusal? Refusal);

/// <summary>What a change that may not leave a team without a manager found.</summary>
public enum ManagerGuardedChange
{
    Changed,
    NotFound,
    LastManager,
}

/// <summary>What deleting a team found.</summary>
public enum TeamDeletion
{
    Deleted,
    NotFound,
    Refused,
}
