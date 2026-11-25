using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITeamRepository"/>, one context per call. The caller's own list reads pinned to the
/// caller; everything else reads as the platform, since a team's other members are hidden from a
/// pinned context, and names the caller's membership in its own query.
/// </summary>
public partial class TeamRepository(INocturneDbContextFactory contexts) : ITeamRepository
{
    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TeamSummary>> GetTeamsOfAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        return await db.Teams.AsNoTracking()
            .Where(t => t.Members.Any(m => m.SubjectId == subjectId))
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Select(t => new TeamSummary(
                t.Id, t.Name, t.Members.Where(m => m.SubjectId == subjectId).Select(m => m.Kind).First()))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<TeamSummary> CreateAsync(Guid creatorSubjectId, string name, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var team = new TeamEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreatedBySubjectId = creatorSubjectId,
        };
        db.Teams.Add(team);
        db.TeamMembers.Add(new TeamMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TeamId = team.Id,
            SubjectId = creatorSubjectId,
            Kind = TeamMemberKind.Manager,
        });
        TeamAudit.StageCreated(db, team.Id, name);
        TeamAudit.StageManagerAdded(db, team.Id, creatorSubjectId, inviteId: null);
        await db.SaveChangesAsync(ct);
        return new TeamSummary(team.Id, team.Name, TeamMemberKind.Manager);
    }

    /// <inheritdoc/>
    public virtual async Task<TeamDetail?> GetAsync(Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var team = await db.Teams.AsNoTracking()
            .Where(t => t.Id == teamId && t.Members.Any(m => m.SubjectId == callerSubjectId))
            .Select(t => new
            {
                t.Id,
                t.Name,
                Caller = t.Members
                    .Where(m => m.SubjectId == callerSubjectId)
                    .Select(m => new { m.Kind, m.RoleCeilingSlug, m.LimitTo24Hours, m.EndsAt })
                    .First(),
                Managers = t.Members
                    .Where(m => m.Kind == TeamMemberKind.Manager)
                    .OrderBy(m => m.SysCreatedAt)
                    .ThenBy(m => m.Id)
                    .Select(m => new TeamManager(m.SubjectId, m.Subject!.Name))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (team is null)
            return null;

        var isManager = team.Caller.Kind == TeamMemberKind.Manager;
        IReadOnlyList<TeamManagerInvite> invites = isManager
            ? await LiveLinks(db, db.TeamInvites, teamId, now)
                .OrderBy(i => i.SysCreatedAt)
                .Select(i => new TeamManagerInvite(
                    i.Id, i.Label, i.ExpiresAt, i.MaxUses, i.UseCount, i.CreatedBySubjectId, i.CreatedBy!.Name))
                .ToListAsync(ct)
            : [];

        return new TeamDetail(
            team.Id,
            team.Name,
            isManager ? team.Managers : [],
            team.Managers.Select(m => m.DisplayName).ToList(),
            invites,
            isManager ? await StaffOfAsync(db, teamId, now, ct) : [],
            isManager ? await LiveStaffInvitesAsync(db, teamId, now, ct) : [],
            isManager ? await LiveAccessLinksAsync(db, teamId, now, ct) : [],
            isManager ? await SitesOfAsync(db, teamId, now, ct) : [],
            TeamSuggestableRoles.All,
            await db.IsLastManagerAsync(teamId, callerSubjectId, ct),
            await CanDeleteAsync(db, teamId, callerSubjectId, ct),
            team.Caller.Kind,
            team.Caller.Kind == TeamMemberKind.Staff
                ? new TeamStaffSelf(
                    new TeamStaffNarrowing(team.Caller.RoleCeilingSlug, team.Caller.LimitTo24Hours, Utc(team.Caller.EndsAt)),
                    MembershipLiveness.HasEnded(team.Caller.EndsAt, now))
                : null);
    }

    /// <inheritdoc/>
    public virtual Task<string?> RenameAsync(Guid teamId, Guid callerSubjectId, string name, CancellationToken ct) =>
        AsManagerAsync<string?>(teamId, callerSubjectId, null, async (db, token) =>
        {
            var team = await db.Teams.FirstAsync(t => t.Id == teamId, token);
            var oldName = team.Name;
            if (oldName != name)
            {
                TeamAudit.StageRenamed(db, team.Id, oldName, name);
                team.Name = name;
                await db.SaveChangesAsync(token);
            }

            return oldName;
        }, ct);

    /// <inheritdoc/>
    public virtual async Task<ManagerGuardedChange> RemoveManagerAsync(
        Guid teamId, Guid callerSubjectId, Guid managerSubjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockTeamManagersAsync(teamId, token);

            if (!await IsManagerAsync(db, teamId, callerSubjectId, token))
                return ManagerGuardedChange.NotFound;

            var manager = await db.TeamMembers.FirstOrDefaultAsync(
                m => m.TeamId == teamId && m.SubjectId == managerSubjectId && m.Kind == TeamMemberKind.Manager, token);
            if (manager is null)
                return ManagerGuardedChange.NotFound;

            if (await db.IsLastManagerAsync(teamId, managerSubjectId, token))
                return ManagerGuardedChange.LastManager;

            db.TeamMembers.Remove(manager);
            TeamAudit.StageManagerRemoved(db, teamId, managerSubjectId);

            await RevokeLinksMadeByAsync(db, db.TeamInvites, teamId, managerSubjectId, now, TeamAudit.StageInviteRevoked, token);
            await RevokeLinksMadeByAsync(db, db.TeamStaffInvites, teamId, managerSubjectId, now, TeamAudit.StageStaffInviteRevoked, token);
            await RevokeLinksMadeByAsync(db, db.TeamAccessLinks, teamId, managerSubjectId, now, TeamAudit.StageAccessLinkRevoked, token);

            await db.SaveChangesAsync(token);
            return ManagerGuardedChange.Changed;
        }, ct: ct);
    }

    /// <inheritdoc/>
    public virtual Task<Guid?> CreateInviteAsync(
        Guid teamId, Guid callerSubjectId, NewTeamInvite invite, CancellationToken ct) =>
        AsManagerAsync<Guid?>(teamId, callerSubjectId, null, async (db, token) =>
        {
            var entity = new TeamInviteEntity
            {
                Id = Guid.CreateVersion7(),
                TeamId = teamId,
                Kind = TeamMemberKind.Manager,
                CreatedBySubjectId = callerSubjectId,
                TokenHash = invite.TokenHash,
                Label = invite.Label,
                ExpiresAt = invite.ExpiresAt,
                MaxUses = invite.MaxUses,
            };
            db.TeamInvites.Add(entity);
            TeamAudit.StageInviteCreated(db, teamId, entity.Id);
            await db.SaveChangesAsync(token);
            return entity.Id;
        }, ct);

    /// <inheritdoc/>
    public virtual Task<bool> RevokeInviteAsync(
        Guid teamId, Guid callerSubjectId, Guid inviteId, DateTime now, CancellationToken ct) =>
        RevokeLinkAsync(teamId, callerSubjectId, inviteId, now, db => db.TeamInvites, TeamAudit.StageInviteRevoked, ct);

    /// <inheritdoc/>
    public virtual async Task<TeamInviteInfo?> GetInviteInfoAsync(
        string tokenHash, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var invite = await db.TeamInvites.AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => new
            {
                Invite = i,
                TeamName = i.Team!.Name,
                CreatedByName = i.CreatedBy!.Name,
                IsMember = i.Team.Members.Any(m => m.SubjectId == subjectId),
            })
            .FirstOrDefaultAsync(ct);
        if (invite is null)
            return null;

        var refusal = await RefusalAsync(db, invite.Invite, now, ct)
            ?? (invite.IsMember ? TeamInviteRefusal.AlreadyMember : null);
        return new TeamInviteInfo(invite.Invite.TeamId, invite.TeamName, invite.CreatedByName, invite.Invite.Kind, refusal);
    }

    /// <inheritdoc/>
    public virtual async Task<TeamInviteAcceptance> AcceptInviteAsync(
        string tokenHash, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            var teamId = await db.TeamInvites.AsNoTracking()
                .Where(i => i.TokenHash == tokenHash)
                .Select(i => (Guid?)i.TeamId)
                .FirstOrDefaultAsync(token);
            if (teamId is not { } id)
                return new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound);

            await db.LockSubjectRowSharedAsync(subjectId, token);
            await db.LockTeamManagersAsync(id, token);

            var invite = await db.TeamInvites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, token);
            if (invite is null)
                return new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound);
            if (await RefusalAsync(db, invite, now, token) is { } refusal)
                return new TeamInviteAcceptance(id, refusal);
            if (await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.SubjectId == subjectId, token))
                return new TeamInviteAcceptance(id, TeamInviteRefusal.AlreadyMember);

            invite.UseCount++;
            db.TeamMembers.Add(new TeamMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TeamId = id,
                SubjectId = subjectId,
                Kind = TeamMemberKind.Manager,
            });
            TeamAudit.StageManagerAdded(db, id, subjectId, invite.Id);
            await db.SaveChangesAsync(token);
            return new TeamInviteAcceptance(id, null);
        }, ct: ct);
    }

    /// <summary>
    /// Removes the team, whose members, invites, access links and access to sites go with it by
    /// cascade, and stages its audit row and a stopped row for each site it had access to. The caller
    /// holds the team's manager lock and an open transaction, and has removed every team reason that
    /// stands on the team's access: a deletion (<see cref="LockTeamDeletionAsync"/>) removes them, and
    /// account deletion removes only teams with no member but the subject, whose memberships it removed.
    /// </summary>
    internal static async Task RemoveAsync(NocturneDbContext db, TeamEntity team, CancellationToken ct)
    {
        foreach (var access in await AccessOfTeamAcrossSitesAsync(db, team.Id, ct))
            TeamAudit.StageAccessStopped(db, access.Access, team.Name, access.RoleSlugs, TeamAudit.TeamDeletedHow);

        db.Teams.Remove(team);
        TeamAudit.StageDeleted(db, team.Id, team.Name);
    }

    /// <summary>
    /// Whether the caller may delete the team now: any manager may, while the team gives no one
    /// access to anything.
    /// </summary>
    private static Task<bool> CanDeleteAsync(NocturneDbContext db, Guid teamId, Guid callerSubjectId, CancellationToken ct) =>
        IsManagerAsync(db, teamId, callerSubjectId, ct);

    private static Task<bool> IsManagerAsync(NocturneDbContext db, Guid teamId, Guid subjectId, CancellationToken ct) =>
        db.TeamMembers.AnyAsync(
            m => m.TeamId == teamId && m.SubjectId == subjectId && m.Kind == TeamMemberKind.Manager, ct);

    /// <summary>
    /// Runs <paramref name="write"/> under the team's manager lock, in one transaction with the check
    /// that the caller manages the team, so a manager removed meanwhile cannot write once the removal
    /// commits. <paramref name="notManager"/> is the answer when the caller does not manage it. The
    /// caller's subject row is locked first, since a write may name the caller as its maker
    /// (<see cref="MembershipLock"/>).
    /// </summary>
    private async Task<T> AsManagerAsync<T>(
        Guid teamId, Guid callerSubjectId, T notManager,
        Func<NocturneDbContext, CancellationToken, Task<T>> write, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockSubjectRowSharedAsync(callerSubjectId, token);
            await db.LockTeamManagersAsync(teamId, token);
            return await IsManagerAsync(db, teamId, callerSubjectId, token)
                ? await write(db, token)
                : notManager;
        }, ct: ct);
    }

    /// <summary>
    /// Revokes the team's link <paramref name="linkId"/> as a manager of the team, once: a link already
    /// revoked is found and left as it was. False when the caller does not manage the team or it has no
    /// such link.
    /// </summary>
    private Task<bool> RevokeLinkAsync<T>(
        Guid teamId, Guid callerSubjectId, Guid linkId, DateTime now,
        Func<NocturneDbContext, DbSet<T>> links, Action<NocturneDbContext, Guid, Guid> stageRevoked, CancellationToken ct)
        where T : class, ITeamLink =>
        AsManagerAsync(teamId, callerSubjectId, false, async (db, token) =>
        {
            var link = await links(db).FirstOrDefaultAsync(l => l.Id == linkId && l.TeamId == teamId, token);
            if (link is null)
                return false;

            if (link.RevokedAt is null)
            {
                link.RevokedAt = now;
                stageRevoked(db, teamId, linkId);
                await db.SaveChangesAsync(token);
            }

            return true;
        }, ct);

    /// <summary>
    /// Revokes at <paramref name="now"/> the live links of one kind that <paramref name="makerSubjectId"/>
    /// made, since a link lives only while its maker manages the team. The caller saves.
    /// </summary>
    private static async Task RevokeLinksMadeByAsync<T>(
        NocturneDbContext db, DbSet<T> links, Guid teamId, Guid makerSubjectId, DateTime now,
        Action<NocturneDbContext, Guid, Guid> stageRevoked, CancellationToken ct)
        where T : class, ITeamLink
    {
        var madeByThem = await LiveLinks(db, links, teamId, now)
            .Where(l => l.CreatedBySubjectId == makerSubjectId)
            .ToListAsync(ct);
        foreach (var link in madeByThem)
        {
            link.RevokedAt = now;
            stageRevoked(db, teamId, link.Id);
        }
    }

    /// <summary>
    /// The team's links of one kind (a manager invite, a staff invite or an access link) that can still be used at <paramref name="now"/>, made by a manager
    /// who still manages the team: the query form of <see cref="RefusalAsync"/>.
    /// </summary>
    private static IQueryable<T> LiveLinks<T>(NocturneDbContext db, IQueryable<T> links, Guid teamId, DateTime now)
        where T : class, ITeamLink =>
        links.Where(l => l.TeamId == teamId
            && l.RevokedAt == null
            && l.ExpiresAt > now
            && (l.MaxUses == null || l.UseCount < l.MaxUses)
            && db.TeamMembers.Any(m => m.TeamId == teamId
                && m.SubjectId == l.CreatedBySubjectId
                && m.Kind == TeamMemberKind.Manager));

    /// <summary>
    /// Why a team's link, a manager invite, a staff invite or an access link, cannot be used at
    /// <paramref name="now"/>, or null when it can. A link lives only while the manager who made it manages the team.
    /// </summary>
    private static async Task<TeamInviteRefusal?> RefusalAsync(
        NocturneDbContext db, ITeamLink link, DateTime now, CancellationToken ct) =>
        link.RevokedAt is not null ? TeamInviteRefusal.Revoked
        : link.ExpiresAt <= now ? TeamInviteRefusal.Expired
        : link.MaxUses is { } cap && link.UseCount >= cap ? TeamInviteRefusal.UsedUp
        : !await IsManagerAsync(db, link.TeamId, link.CreatedBySubjectId, ct) ? TeamInviteRefusal.NoLongerValid
        : null;
}
