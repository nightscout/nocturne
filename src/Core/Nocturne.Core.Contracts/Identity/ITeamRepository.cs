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
    /// reads its invites.
    /// </summary>
    Task<TeamDetail?> GetAsync(Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>Renames the team. False when the caller is not a manager of it.</summary>
    Task<bool> RenameAsync(Guid teamId, Guid callerSubjectId, string name, CancellationToken ct);

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

    /// <summary>The team an invite is for, as <paramref name="subjectId"/> holding its token sees it.</summary>
    Task<TeamInviteInfo?> GetInviteInfoAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Makes <paramref name="subjectId"/> a manager of the invite's team and takes one use of the
    /// invite. A subject already a member changes nothing and is told so. Refused with
    /// <see cref="TeamInviteRefusal.NoLongerValid"/> once the invite's maker no longer manages the team.
    /// </summary>
    Task<TeamInviteAcceptance> AcceptInviteAsync(string tokenHash, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Deletes the team with its members and invites. <see cref="TeamDeletion.NotFound"/> when the
    /// caller is not a member, <see cref="TeamDeletion.Refused"/> when the team may not be deleted now.
    /// </summary>
    Task<TeamDeletion> DeleteAsync(Guid teamId, Guid callerSubjectId, CancellationToken ct);
}
