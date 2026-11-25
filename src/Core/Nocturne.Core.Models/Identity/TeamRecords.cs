namespace Nocturne.Core.Models.Identity;

/// <summary>What a member of a team is there as.</summary>
public static class TeamMemberKind
{
    /// <summary>Runs the team: renames it, adds and removes managers, and deletes it.</summary>
    public const string Manager = "manager";

    /// <summary>
    /// Works for the team and reaches the people it follows, within what narrows them. Runs nothing in
    /// the team and reads nothing of it but its name and its managers' names.
    /// </summary>
    public const string Staff = "staff";
}

/// <summary>Whether a site has approved a staff member of a team that has access to it.</summary>
public static class TeamSiteStaffStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";

    /// <summary>An owner of the site declined the staff member, and only an owner's approval lifts it.</summary>
    public const string Declined = "declined";
}

/// <summary>A team the caller is a member of.</summary>
/// <param name="Kind">What the caller is in the team, a <see cref="TeamMemberKind"/> value.</param>
public sealed record TeamSummary(Guid Id, string Name, string Kind);

/// <summary>One manager of a team.</summary>
public sealed record TeamManager(Guid SubjectId, string DisplayName);

/// <summary>
/// What narrows a staff member of a team, or what a staff invite gives. <paramref name="RoleCeilingSlug"/>
/// is a <see cref="TeamSuggestableRoles"/> slug, null for everything the team holds at a site;
/// <paramref name="EndsAt"/> is in UTC, null for no end.
/// </summary>
public sealed record TeamStaffNarrowing(string? RoleCeilingSlug, bool LimitTo24Hours, DateTime? EndsAt);

/// <summary>
/// A staff member of a team, as a manager of the team reads them.
/// </summary>
/// <param name="HasEnded">Whether <see cref="TeamStaffNarrowing.EndsAt"/> has passed.</param>
/// <param name="SitesApproved">The sites the team follows where the staff member is approved.</param>
/// <param name="SitesPending">The sites the team follows where the staff member waits for approval.</param>
public sealed record TeamStaffMember(
    Guid SubjectId,
    string DisplayName,
    TeamStaffNarrowing Narrowing,
    bool HasEnded,
    int SitesApproved,
    int SitesPending);

/// <summary>A staff invite that can still be accepted, as a manager reads it. The token is never read back.</summary>
public sealed record TeamStaffInviteSummary(
    Guid Id,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    Guid CreatedBySubjectId,
    string CreatedByName,
    TeamStaffNarrowing Narrowing);

/// <summary>What narrows the caller, a staff member of the team, and whether their end has passed.</summary>
public sealed record TeamStaffSelf(TeamStaffNarrowing Narrowing, bool HasEnded);

/// <summary>
/// A manager invite that can still be accepted, and the manager who made it. The token is never read back.
/// </summary>
public sealed record TeamManagerInvite(
    Guid Id, string? Label, DateTime ExpiresAt, int? MaxUses, int UseCount, Guid CreatedBySubjectId, string CreatedByName);

/// <summary>
/// A team as one of its members reads it. A staff member reads its name, its managers' names, their
/// own kind, <paramref name="OwnStaff"/> and the static <paramref name="SuggestableRoles"/> that name
/// their role ceiling, and nothing else.
/// </summary>
/// <param name="Managers">The managers with their subject ids; empty unless the caller is a manager.</param>
/// <param name="ManagerNames">The managers' display names, for any member.</param>
/// <param name="IsLastManager">
/// Whether the caller is the manager the team cannot lose, so they cannot leave and no one can remove
/// them.
/// </param>
/// <param name="Invites">The live manager invites; empty unless the caller is a manager.</param>
/// <param name="Staff">The team's staff; empty unless the caller is a manager.</param>
/// <param name="StaffInvites">The live staff invites; empty unless the caller is a manager.</param>
/// <param name="AccessLinks">The live access links; empty unless the caller is a manager.</param>
/// <param name="Sites">The sites the team has access to; empty unless the caller is a manager.</param>
/// <param name="SuggestableRoles">The roles an access link may suggest and a role ceiling may name, for any member.</param>
/// <param name="CanDelete">Whether the caller may delete the team now.</param>
/// <param name="CallerKind">What the caller is in the team, a <see cref="TeamMemberKind"/> value.</param>
/// <param name="OwnStaff">What narrows the caller; null unless the caller is a staff member.</param>
public sealed record TeamDetail(
    Guid Id,
    string Name,
    IReadOnlyList<TeamManager> Managers,
    IReadOnlyList<string> ManagerNames,
    IReadOnlyList<TeamManagerInvite> Invites,
    IReadOnlyList<TeamStaffMember> Staff,
    IReadOnlyList<TeamStaffInviteSummary> StaffInvites,
    IReadOnlyList<TeamAccessLinkSummary> AccessLinks,
    IReadOnlyList<TeamSiteAccess> Sites,
    IReadOnlyList<TeamSuggestableRole> SuggestableRoles,
    bool IsLastManager,
    bool CanDelete,
    string CallerKind,
    TeamStaffSelf? OwnStaff);

/// <summary>A seed role an access link may suggest, with its permissions as every new site is seeded with it.</summary>
public sealed record TeamSuggestableRole(string Slug, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The roles an access link may suggest and a staff member's role ceiling may name: the seed roles that
/// hold permissions, all for data only.
/// </summary>
public static class TeamSuggestableRoles
{
    public static IReadOnlyList<TeamSuggestableRole> All { get; } = Authorization.RoleSeeds.Permissions
        .Where(r => r.Value.Count > 0 && Authorization.Scope.IsDataOnly(r.Value))
        .Select(r => new TeamSuggestableRole(r.Key, Authorization.RoleSeeds.DisplayNames[r.Key], r.Value))
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The permissions of the suggestable role <paramref name="slug"/>, or null when it is not one.</summary>
    public static IReadOnlyList<string>? PermissionsOf(string slug) =>
        All.FirstOrDefault(r => r.Slug == slug)?.Permissions;

    /// <summary>The name of the suggestable role <paramref name="slug"/>, or null when it is not one.</summary>
    public static string? NameOf(string? slug) =>
        All.FirstOrDefault(r => r.Slug == slug)?.Name;
}

/// <summary>A manager invite as it is stored, before it has an id.</summary>
public sealed record NewTeamInvite(
    string TokenHash,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses);

/// <summary>A staff invite as it is stored, before it has an id.</summary>
public sealed record NewTeamStaffInvite(
    string TokenHash,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    TeamStaffNarrowing Narrowing);

/// <summary>Why a team invite cannot be accepted.</summary>
public enum TeamInviteRefusal
{
    NotFound,
    Revoked,
    Expired,
    UsedUp,

    /// <summary>The manager who made the invite no longer manages the team.</summary>
    NoLongerValid,

    /// <summary>The end the staff invite gives has passed, so what it gives would last no time.</summary>
    StaffEndPassed,

    AlreadyMember,
}

/// <summary>
/// The team a manager invite is for, read by the person holding its token.
/// </summary>
/// <param name="Kind">What accepting makes the person, a <see cref="TeamMemberKind"/> value.</param>
/// <param name="Refusal">Why it cannot be accepted, or null when it can.</param>
public sealed record TeamInviteInfo(
    Guid TeamId, string TeamName, string CreatedByName, string Kind, TeamInviteRefusal? Refusal);

/// <summary>The team a staff invite is for, read by the person holding its token.</summary>
/// <param name="Refusal">Why it cannot be accepted, or null when it can.</param>
public sealed record TeamStaffInviteInfo(
    Guid TeamId, string TeamName, string CreatedByName, TeamStaffNarrowing Narrowing, TeamInviteRefusal? Refusal);

/// <summary>What accepting a team invite did: the team joined, or why it was refused.</summary>
public sealed record TeamInviteAcceptance(Guid? TeamId, TeamInviteRefusal? Refusal);

/// <summary>What a change that may not leave a team without a manager found.</summary>
public enum ManagerGuardedChange
{
    Changed,
    NotFound,
    LastManager,
}

/// <summary>What editing a staff member found.</summary>
public enum TeamStaffChange
{
    Changed,

    /// <summary>The caller does not manage the team, or the subject is not its staff.</summary>
    NotFound,

    /// <summary>The edit sets an end that has passed and is not the end the staff member already had.</summary>
    EndPassed,
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

/// <summary>
/// A site the team has access to, as a manager of the team reads it: how many of its staff wait for the
/// site's approval there and how many the site declined, never which of them or who decided.
/// </summary>
/// <param name="HasEnded">Whether <paramref name="AccessEndsAt"/> has passed.</param>
/// <param name="GivenAt">When the access was given.</param>
/// <param name="TeamManagesStaff">Whether the team's staff are approved at the site as they join.</param>
public sealed record TeamSiteAccess(
    Guid TenantId,
    string DisplayName,
    IReadOnlyList<string> RoleNames,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
    bool HasEnded,
    DateTime GivenAt,
    bool TeamManagesStaff,
    int PendingStaffCount,
    int DeclinedStaffCount);

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
/// <param name="TeamManagesStaff">
/// Whether the team's managers decide which of their staff reach the site: its staff are approved
/// there as they join. When false each waits for the site to approve them.
/// </param>
public sealed record TeamAccessGrant(
    Guid TenantId, IReadOnlyList<Guid> RoleIds, bool LimitTo24Hours, DateTime? AccessEndsAt, bool TeamManagesStaff);

/// <summary>What approving an access link did: the access made, or why it was refused.</summary>
public sealed record TeamAccessApproval(Guid? AccessId, TeamAccessRefusal? Refusal);

/// <summary>Everything a team's access to a site gives, as an owner sets it in place of what it gave.</summary>
/// <param name="AccessEndsAt">When the access ends, in UTC; null for no end.</param>
public sealed record TeamAccessTerms(IReadOnlyList<Guid> RoleIds, bool LimitTo24Hours, DateTime? AccessEndsAt);

/// <summary>
/// What an owner's edit of a team's access to their site found: <paramref name="Refusal"/> says why the new
/// terms were refused when <paramref name="Decision"/> is <see cref="OwnerDecision.Decided"/>, and is null
/// when they were saved or changed nothing.
/// </summary>
public sealed record TeamAccessEdit(OwnerDecision Decision, TeamAccessRefusal? Refusal);

/// <summary>A team with access to a site, as the site reads it.</summary>
/// <param name="HasEnded">Whether <paramref name="AccessEndsAt"/> has passed.</param>
/// <param name="ApprovedBySubjectId">Who approved it; null once that subject is deleted.</param>
/// <param name="TeamManagesStaff">
/// Whether the team's staff are approved at the site as they join. When false each waits for an owner.
/// </param>
/// <param name="Staff">The team's staff, each with whether the site approved them.</param>
public sealed record TenantTeamAccess(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<TenantTeamAccessRole> Roles,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
    bool HasEnded,
    Guid? ApprovedBySubjectId,
    string? ApprovedByName,
    DateTime GivenAt,
    bool TeamManagesStaff,
    IReadOnlyList<TenantTeamStaff> Staff);

/// <summary>A staff member of a team with access to a site, as the site reads them.</summary>
/// <param name="Status">Whether the site approved them, a <see cref="TeamSiteStaffStatus"/> value.</param>
/// <param name="HasEnded">Whether <see cref="TeamStaffNarrowing.EndsAt"/> has passed.</param>
/// <param name="RoleCeilingName">The name of <see cref="TeamStaffNarrowing.RoleCeilingSlug"/>; null when there is no ceiling.</param>
public sealed record TenantTeamStaff(
    Guid SubjectId, string DisplayName, string Status, TeamStaffNarrowing Narrowing, bool HasEnded, string? RoleCeilingName);

/// <summary>
/// What an owner's decision at their site found: about a team's access to it, or about the team's staff there.
/// </summary>
public enum OwnerDecision
{
    Decided,

    /// <summary>The team has no access to the site, or the subject is not its staff.</summary>
    NotFound,

    /// <summary>The caller holds no direct reason in force on the site, so gives nothing.</summary>
    AccessFromTeam,

    /// <summary>The caller's direct reason on the site does not hold the owner role.</summary>
    NotOwner,
}

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
