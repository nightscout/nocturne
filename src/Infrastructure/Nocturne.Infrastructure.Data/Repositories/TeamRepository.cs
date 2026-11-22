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
public class TeamRepository(INocturneDbContextFactory contexts) : ITeamRepository
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
                CallerKind = t.Members.Where(m => m.SubjectId == callerSubjectId).Select(m => m.Kind).First(),
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

        IReadOnlyList<TeamManagerInvite> invites = await IsManagerAsync(db, teamId, callerSubjectId, ct)
            ? await LiveInvites(db, teamId, now)
                .Where(i => db.TeamMembers.Any(m => m.TeamId == teamId
                    && m.SubjectId == i.CreatedBySubjectId
                    && m.Kind == TeamMemberKind.Manager))
                .OrderBy(i => i.SysCreatedAt)
                .Select(i => new TeamManagerInvite(
                    i.Id, i.Label, i.ExpiresAt, i.MaxUses, i.UseCount, i.CreatedBySubjectId, i.CreatedBy!.Name))
                .ToListAsync(ct)
            : [];

        return new TeamDetail(
            team.Id,
            team.Name,
            team.Managers,
            invites,
            await db.IsLastManagerAsync(teamId, callerSubjectId, ct),
            await CanDeleteAsync(db, teamId, callerSubjectId, ct),
            team.CallerKind);
    }

    /// <inheritdoc/>
    public virtual Task<bool> RenameAsync(Guid teamId, Guid callerSubjectId, string name, CancellationToken ct) =>
        AsManagerAsync(teamId, callerSubjectId, false, async (db, token) =>
        {
            var team = await db.Teams.FirstAsync(t => t.Id == teamId, token);
            if (team.Name != name)
            {
                TeamAudit.StageRenamed(db, team.Id, team.Name, name);
                team.Name = name;
                await db.SaveChangesAsync(token);
            }

            return true;
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

            var madeByThem = await LiveInvites(db, teamId, now)
                .Where(i => i.CreatedBySubjectId == managerSubjectId)
                .ToListAsync(token);
            foreach (var invite in madeByThem)
            {
                invite.RevokedAt = now;
                TeamAudit.StageInviteRevoked(db, teamId, invite.Id);
            }

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
        AsManagerAsync(teamId, callerSubjectId, false, async (db, token) =>
        {
            var invite = await db.TeamInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.TeamId == teamId, token);
            if (invite is null)
                return false;

            if (invite.RevokedAt is null)
            {
                invite.RevokedAt = now;
                TeamAudit.StageInviteRevoked(db, teamId, inviteId);
                await db.SaveChangesAsync(token);
            }

            return true;
        }, ct);

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
                Kind = invite.Kind,
            });
            TeamAudit.StageManagerAdded(db, id, subjectId, invite.Id);
            await db.SaveChangesAsync(token);
            return new TeamInviteAcceptance(id, null);
        }, ct: ct);
    }

    /// <inheritdoc/>
    public virtual async Task<TeamDeletion> DeleteAsync(Guid teamId, Guid callerSubjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockTeamManagersAsync(teamId, token);

            var team = await db.Teams.FirstOrDefaultAsync(
                t => t.Id == teamId && t.Members.Any(m => m.SubjectId == callerSubjectId), token);
            if (team is null)
                return TeamDeletion.NotFound;

            if (!await CanDeleteAsync(db, teamId, callerSubjectId, token))
                return TeamDeletion.Refused;

            Remove(db, team);
            await db.SaveChangesAsync(token);
            return TeamDeletion.Deleted;
        }, ct: ct);
    }

    /// <summary>
    /// Removes the team, whose members and invites go with it by cascade, and stages its audit row.
    /// The caller holds the team's manager lock.
    /// </summary>
    internal static void Remove(NocturneDbContext db, TeamEntity team)
    {
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
    /// commits. <paramref name="notManager"/> is the answer when the caller does not manage it.
    /// </summary>
    private async Task<T> AsManagerAsync<T>(
        Guid teamId, Guid callerSubjectId, T notManager,
        Func<NocturneDbContext, CancellationToken, Task<T>> write, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockTeamManagersAsync(teamId, token);
            return await IsManagerAsync(db, teamId, callerSubjectId, token)
                ? await write(db, token)
                : notManager;
        }, ct: ct);
    }

    private static IQueryable<TeamInviteEntity> LiveInvites(NocturneDbContext db, Guid teamId, DateTime now) =>
        db.TeamInvites.Where(i => i.TeamId == teamId
            && i.RevokedAt == null
            && i.ExpiresAt > now
            && (i.MaxUses == null || i.UseCount < i.MaxUses));

    /// <summary>
    /// Why the invite cannot be accepted at <paramref name="now"/>, or null when it can. An invite
    /// lives only while the manager who made it manages the team.
    /// </summary>
    private static async Task<TeamInviteRefusal?> RefusalAsync(
        NocturneDbContext db, TeamInviteEntity invite, DateTime now, CancellationToken ct) =>
        invite.RevokedAt is not null ? TeamInviteRefusal.Revoked
        : invite.ExpiresAt <= now ? TeamInviteRefusal.Expired
        : invite.MaxUses is { } cap && invite.UseCount >= cap ? TeamInviteRefusal.UsedUp
        : !await IsManagerAsync(db, invite.TeamId, invite.CreatedBySubjectId, ct) ? TeamInviteRefusal.NoLongerValid
        : null;
}
