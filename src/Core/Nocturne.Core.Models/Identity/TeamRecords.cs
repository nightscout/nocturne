namespace Nocturne.Core.Models.Identity;

/// <summary>What a member of a team is there as.</summary>
public static class TeamMemberKind
{
    /// <summary>Runs the team: renames it, adds and removes managers, and deletes it.</summary>
    public const string Manager = "manager";
}

/// <summary>A team the caller is a member of.</summary>
/// <param name="Kind">What the caller is in the team, a <see cref="TeamMemberKind"/> value.</param>
public sealed record TeamSummary(Guid Id, string Name, string Kind);

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
/// <param name="AccessLinks">The live access links; empty unless the caller is a manager.</param>
/// <param name="Sites">The sites the team has access to; empty unless the caller is a manager.</param>
/// <param name="CanDelete">Whether the caller may delete the team now.</param>
/// <param name="CallerKind">What the caller is in the team, a <see cref="TeamMemberKind"/> value.</param>
public sealed record TeamDetail(
    Guid Id,
    string Name,
    IReadOnlyList<TeamManager> Managers,
    IReadOnlyList<TeamManagerInvite> Invites,
    IReadOnlyList<TeamAccessLinkSummary> AccessLinks,
    IReadOnlyList<TeamSiteAccess> Sites,
    bool IsLastManager,
    bool CanDelete,
    string CallerKind);

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
/// <param name="Kind">What accepting makes the person, a <see cref="TeamMemberKind"/> value.</param>
/// <param name="Refusal">Why it cannot be accepted, or null when it can.</param>
public sealed record TeamInviteInfo(
    Guid TeamId, string TeamName, string CreatedByName, string Kind, TeamInviteRefusal? Refusal);

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

/// <summary>
/// What a "give this team access" link suggests. A person who opens it may narrow it and never widen it.
/// </summary>
/// <param name="RoleSlug">A <see cref="Authorization.RoleSeeds"/> slug, since roles are per site.</param>
/// <param name="AccessEndsAt">The latest the access may end, in UTC; null for no end.</param>
public sealed record TeamAccessSuggestion(string RoleSlug, bool LimitTo24Hours, DateTime? AccessEndsAt);

/// <summary>An access link as it is stored, before it has an id.</summary>
public sealed record NewTeamAccessLink(
    string TokenHash,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    TeamAccessSuggestion Suggestion);

/// <summary>An access link that can still be used, as a manager of the team reads it.</summary>
public sealed record TeamAccessLinkSummary(
    Guid Id,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    Guid CreatedBySubjectId,
    string CreatedByName,
    TeamAccessSuggestion Suggestion);

/// <summary>A site the team has access to, as a manager of the team reads it.</summary>
/// <param name="GivenAt">When the access was given.</param>
public sealed record TeamSiteAccess(
    Guid TenantId,
    string DisplayName,
    IReadOnlyList<string> RoleNames,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
    DateTime GivenAt);

/// <summary>An access link, read by the person holding its token.</summary>
/// <param name="Refusal">Why it cannot be used, or null when it can.</param>
public sealed record TeamAccessLinkInfo(
    Guid LinkId,
    Guid TeamId,
    string TeamName,
    string CreatedByName,
    TeamAccessSuggestion Suggestion,
    DateTime ExpiresAt,
    TeamAccessRefusal? Refusal);

/// <summary>Why a team cannot be given access to a site through a link.</summary>
public enum TeamAccessRefusal
{
    NotFound,
    Revoked,
    Expired,
    UsedUp,

    /// <summary>The manager who made the link no longer manages the team.</summary>
    NoLongerValid,

    /// <summary>The team already has access to the site.</summary>
    AlreadyGiven,

    /// <summary>The person is not an owner of the site.</summary>
    NotOwner,

    /// <summary>No role was chosen.</summary>
    NoRoles,

    /// <summary>A chosen role is not one of the site's.</summary>
    RoleNotInTenant,

    /// <summary>A chosen role holds a permission that runs the site (<see cref="Authorization.Scope.IsDataOnly"/>).</summary>
    RoleAdministrative,

    /// <summary>A chosen role holds a permission the link's suggested role does not.</summary>
    RoleWiderThanLink,

    /// <summary>A chosen role holds a permission the person does not.</summary>
    RoleWiderThanApprover,

    /// <summary>The link or the person's own access requires the 24-hour limit.</summary>
    LimitRequired,

    /// <summary>The end is later than the link allows, or there is none when the link sets one.</summary>
    EndLaterThanLink,

    /// <summary>The end is later than the person's own access, or there is none when theirs ends.</summary>
    EndOutlastsApprover,

    /// <summary>The end is not in the future.</summary>
    EndInPast,

    /// <summary>The latest end the link allows has passed, so the link can give nothing.</summary>
    SuggestedEndPassed,
}

/// <summary>What a team is given on a site.</summary>
/// <param name="AccessEndsAt">When the access ends, in UTC; null for no end.</param>
public sealed record TeamAccessGrant(
    Guid TenantId, IReadOnlyList<Guid> RoleIds, bool LimitTo24Hours, DateTime? AccessEndsAt);

/// <summary>What approving an access link did: the access made, or why it was refused.</summary>
public sealed record TeamAccessApproval(Guid? AccessId, TeamAccessRefusal? Refusal);

/// <summary>A team with access to a site, as the site reads it.</summary>
/// <param name="HasEnded">Whether <paramref name="AccessEndsAt"/> has passed.</param>
/// <param name="ApprovedBySubjectId">Who approved it; null once that subject is deleted.</param>
public sealed record TenantTeamAccess(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<TenantTeamAccessRole> Roles,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
    bool HasEnded,
    Guid? ApprovedBySubjectId,
    string? ApprovedByName,
    DateTime GivenAt);

/// <summary>A role a team's access gives.</summary>
public sealed record TenantTeamAccessRole(Guid Id, string Name, string Slug);

/// <summary>Who approved a team's access to a site that still exists, and the team's current name.</summary>
public sealed record TeamAccessApprover(Guid TenantId, Guid SubjectId, string TeamName);

/// <summary>
/// The signed-in person approving a link, and the credential they present: its own
/// <paramref name="Scopes"/>, ignored for an interactive session, and whether it itself reads only the
/// last 24 hours.
/// </summary>
public sealed record TeamAccessApproverCredential(
    Guid SubjectId, Authorization.AuthType AuthType, IReadOnlySet<string> Scopes, bool LimitTo24Hours);
