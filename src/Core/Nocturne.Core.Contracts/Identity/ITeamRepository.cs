using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// Teams, their members and their manager invites. A team belongs to no tenant.
/// </summary>
/// <remarks>
/// A subject reaches only the teams they are a member of. Every read of one team and every write
/// names the caller, and finds nothing when the caller is not a member, so a non-member cannot tell
/// a team they are not in from one that does not exist. The list of the caller's teams reads on a
/// context pinned to the caller, so the <c>subject_pin</c> policy on <c>teams</c> and
/// <c>team_members</c> holds it to their own even if its query did not.
/// </remarks>
public interface ITeamRepository
{
    /// <summary>The teams <paramref name="subjectId"/> is a member of, by name.</summary>
    Task<IReadOnlyList<TeamSummary>> GetTeamsOfAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Creates a team whose one manager is <paramref name="creatorSubjectId"/>.</summary>
    Task<TeamSummary> CreateAsync(Guid creatorSubjectId, string name, CancellationToken ct);

    /// <summary>
    /// The team, or null when <paramref name="callerSubjectId"/> is not a member of it. Only a manager
    /// reads its managers' subject ids, its staff, its invites, its access links and the sites it has
    /// access to; a staff member reads its name, its managers' names and what narrows them.
    /// </summary>
    Task<TeamDetail?> GetAsync(Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>Renames the team and returns the name it had; null when the caller is not a manager of it.</summary>
    Task<string?> RenameAsync(Guid teamId, Guid callerSubjectId, string name, CancellationToken ct);

    /// <summary>
    /// Removes <paramref name="managerSubjectId"/> from the team; the caller's own id means leave.
    /// Refused with <see cref="ManagerGuardedChange.LastManager"/> when the team would have no
    /// manager who counts, and <see cref="ManagerGuardedChange.NotFound"/> when either the caller or
    /// the manager is not a manager of it. Revokes at <paramref name="now"/> the live invites the
    /// manager made, since an invite lives only while its maker manages the team.
    /// </summary>
    Task<ManagerGuardedChange> RemoveManagerAsync(
        Guid teamId, Guid callerSubjectId, Guid managerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>Stores a manager invite and returns its id; null when the caller is not a manager.</summary>
    Task<Guid?> CreateInviteAsync(Guid teamId, Guid callerSubjectId, NewTeamInvite invite, CancellationToken ct);

    /// <summary>
    /// Revokes the invite. False when the caller is not a manager of the team or the team has no such
    /// invite; revoking a revoked invite succeeds.
    /// </summary>
    Task<bool> RevokeInviteAsync(Guid teamId, Guid callerSubjectId, Guid inviteId, DateTime now, CancellationToken ct);

    /// <summary>Stores a staff invite and returns its id; null when the caller is not a manager.</summary>
    Task<Guid?> CreateStaffInviteAsync(Guid teamId, Guid callerSubjectId, NewTeamStaffInvite invite, CancellationToken ct);

    /// <summary>
    /// Revokes the staff invite. False when the caller is not a manager of the team or the team has no
    /// such invite; revoking a revoked invite succeeds.
    /// </summary>
    Task<bool> RevokeStaffInviteAsync(Guid teamId, Guid callerSubjectId, Guid inviteId, DateTime now, CancellationToken ct);

    /// <summary>The team a staff invite is for, as <paramref name="subjectId"/> holding its token sees it.</summary>
    Task<TeamStaffInviteInfo?> GetStaffInviteInfoAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Makes <paramref name="subjectId"/> staff of the invite's team with what the invite gives, takes
    /// one use of the invite, and writes a <see cref="TeamSiteStaffStatus"/> row for each site the team has
    /// access to. A subject already a member of either kind changes nothing and is told so. A manager
    /// token is <see cref="TeamInviteRefusal.NotFound"/> here, as a staff token is to
    /// <see cref="AcceptInviteAsync"/>. Gives no access to any site.
    /// </summary>
    Task<TeamInviteAcceptance> AcceptStaffInviteAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Sets what narrows the staff member <paramref name="staffSubjectId"/>. An edit that gives them
    /// more than they held (<c>TeamStaffRules.Widens</c>) returns every approval of theirs that the
    /// site, not the team, decides to pending. <see cref="TeamStaffChange.NotFound"/> when the caller is
    /// not a manager of the team or the subject is not its staff, and <see cref="TeamStaffChange.EndPassed"/>
    /// when the end at <paramref name="now"/> has passed and is not the end they already have, so a
    /// manager can still narrow a staff member whose end has passed without moving it.
    /// </summary>
    Task<TeamStaffChange> UpdateStaffAsync(
        Guid teamId, Guid callerSubjectId, Guid staffSubjectId, TeamStaffNarrowing narrowing, DateTime now, CancellationToken ct);

    /// <summary>The team an invite is for, as <paramref name="subjectId"/> holding its token sees it.</summary>
    Task<TeamInviteInfo?> GetInviteInfoAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Makes <paramref name="subjectId"/> a manager of the invite's team and takes one use of the
    /// invite. A subject already a member changes nothing and is told so. Refused with
    /// <see cref="TeamInviteRefusal.NoLongerValid"/> once the invite's maker no longer manages the team.
    /// </summary>
    Task<TeamInviteAcceptance> AcceptInviteAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Stores a "give this team access" link and returns its id; null when the caller is not a manager.
    /// </summary>
    Task<Guid?> CreateAccessLinkAsync(Guid teamId, Guid callerSubjectId, NewTeamAccessLink link, CancellationToken ct);

    /// <summary>
    /// Revokes the access link. False when the caller is not a manager of the team or the team has no
    /// such link; revoking a revoked link succeeds.
    /// </summary>
    Task<bool> RevokeAccessLinkAsync(Guid teamId, Guid callerSubjectId, Guid linkId, DateTime now, CancellationToken ct);

    /// <summary>The access link and its team as the person holding its token sees it, or null when there is none.</summary>
    Task<TeamAccessLinkInfo?> GetAccessLinkInfoAsync(string tokenHash, DateTime now, CancellationToken ct);

    /// <summary>Of <paramref name="tenantIds"/>, the sites the team already has access to.</summary>
    Task<IReadOnlySet<Guid>> ListSitesWithAccessAsync(Guid teamId, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct);

    /// <summary>
    /// Gives the link's team access to <see cref="TeamAccessGrant.TenantId"/> and takes one use of the
    /// link, as <paramref name="approver"/>. Everything is decided in its own transaction under the
    /// site's owners lock, the team's manager lock and the site's team access lock, from what those
    /// locks hold still: the link, the approver's live membership of the site (an owner, and what its
    /// direct reason gives: the scopes it resolves to on the credential, its end and its 24-hour limit,
    /// never what a team reason beside it gives), and the chosen roles. Refused,
    /// taking no use and writing nothing, on any <see cref="TeamAccessRefusal"/>.
    /// </summary>
    Task<TeamAccessApproval> ApproveAccessAsync(
        string tokenHash, TeamAccessApproverCredential approver, TeamAccessGrant grant, DateTime now, CancellationToken ct);

    /// <summary>The teams with access to <paramref name="tenantId"/>, by name.</summary>
    Task<IReadOnlyList<TenantTeamAccess>> ListAccessOfTenantAsync(Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>The roles of <paramref name="tenantId"/> a team's access gives.</summary>
    Task<IReadOnlySet<Guid>> ListRolesHeldByTeamsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Who approved each of the team's accesses that still exist, where that subject still exists,
    /// with the team's name as it is now.
    /// </summary>
    Task<IReadOnlyList<TeamAccessApprover>> ListAccessApproversAsync(Guid teamId, CancellationToken ct);
}
