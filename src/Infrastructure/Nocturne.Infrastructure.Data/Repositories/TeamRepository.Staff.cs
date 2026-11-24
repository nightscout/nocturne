using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// A team's staff: the invites a manager makes, the narrowing a manager sets, and the
/// <c>team_site_staff</c> row each staff member has for each site the team follows. Writes the
/// team's own rows only: no membership of any site and no reason for one. Removing a staff member
/// removes the reasons it ends (<see cref="LockStaffRemovalAsync"/>).
/// </summary>
/// <remarks>
/// Every write here holds the team's manager lock, which an approval of the team's access holds too, so
/// a join and an approval each see the other's rows. A join also takes the access locks of every site
/// the team follows (<see cref="LockAccessOfTeamsAsync"/>), in the order <see cref="MembershipLock"/>
/// gives every write that takes them. So a join either reads the access before a stop commits and has its
/// row removed by the stop's cascade, or waits and reads it gone. An edit takes the same locks, so the
/// rows it loads cannot be removed by a stop before it saves.
/// </remarks>
public partial class TeamRepository
{
    /// <inheritdoc/>
    public virtual Task<Guid?> CreateStaffInviteAsync(
        Guid teamId, Guid callerSubjectId, NewTeamStaffInvite invite, CancellationToken ct) =>
        AsManagerAsync<Guid?>(teamId, callerSubjectId, null, async (db, token) =>
        {
            var entity = new TeamStaffInviteEntity
            {
                Id = Guid.CreateVersion7(),
                TeamId = teamId,
                CreatedBySubjectId = callerSubjectId,
                TokenHash = invite.TokenHash,
                Label = invite.Label,
                ExpiresAt = invite.ExpiresAt,
                MaxUses = invite.MaxUses,
                RoleCeilingSlug = invite.Narrowing.RoleCeilingSlug,
                LimitTo24Hours = invite.Narrowing.LimitTo24Hours,
                StaffEndsAt = invite.Narrowing.EndsAt,
            };
            db.TeamStaffInvites.Add(entity);
            TeamAudit.StageStaffInviteCreated(db, teamId, entity.Id);
            await db.SaveChangesAsync(token);
            return entity.Id;
        }, ct);

    /// <inheritdoc/>
    public virtual Task<bool> RevokeStaffInviteAsync(
        Guid teamId, Guid callerSubjectId, Guid inviteId, DateTime now, CancellationToken ct) =>
        RevokeLinkAsync(teamId, callerSubjectId, inviteId, now, db => db.TeamStaffInvites, TeamAudit.StageStaffInviteRevoked, ct);

    /// <inheritdoc/>
    public virtual async Task<TeamStaffInviteInfo?> GetStaffInviteInfoAsync(
        string tokenHash, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var invite = await db.TeamStaffInvites.AsNoTracking()
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
            ?? StaffEndRefusal(invite.Invite, now)
            ?? (invite.IsMember ? TeamInviteRefusal.AlreadyMember : null);
        return new TeamStaffInviteInfo(
            invite.Invite.TeamId, invite.TeamName, invite.CreatedByName, NarrowingOf(invite.Invite), refusal);
    }

    /// <inheritdoc/>
    public virtual async Task<TeamInviteAcceptance> AcceptStaffInviteAsync(
        string tokenHash, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            var teamId = await db.TeamStaffInvites.AsNoTracking()
                .Where(i => i.TokenHash == tokenHash)
                .Select(i => (Guid?)i.TeamId)
                .FirstOrDefaultAsync(token);
            if (teamId is not { } id)
                return new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound);

            await db.LockTeamManagersAsync(id, token);
            await LockAccessOfTeamsAsync(db, [id], token);

            var invite = await db.TeamStaffInvites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, token);
            if (invite is null)
                return new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound);
            if (await RefusalAsync(db, invite, now, token) is { } refusal)
                return new TeamInviteAcceptance(id, refusal);
            if (StaffEndRefusal(invite, now) is { } ended)
                return new TeamInviteAcceptance(id, ended);
            if (await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.SubjectId == subjectId, token))
                return new TeamInviteAcceptance(id, TeamInviteRefusal.AlreadyMember);

            invite.UseCount++;
            var member = new TeamMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TeamId = id,
                SubjectId = subjectId,
                Kind = TeamMemberKind.Staff,
                RoleCeilingSlug = invite.RoleCeilingSlug,
                LimitTo24Hours = invite.LimitTo24Hours,
                EndsAt = invite.StaffEndsAt,
                CreatedFromStaffInviteId = invite.Id,
            };
            db.TeamMembers.Add(member);

            var accesses = await db.TeamTenantAccess.AsNoTracking()
                .Where(a => a.TeamId == id)
                .Select(a => new { a.Id, a.TeamManagesStaff })
                .ToListAsync(token);
            foreach (var access in accesses)
                db.TeamSiteStaff.Add(NewSiteStaff(access.Id, member.Id, access.TeamManagesStaff));

            TeamAudit.StageStaffAdded(db, id, subjectId, invite.Id);
            await db.SaveChangesAsync(token);
            return new TeamInviteAcceptance(id, null);
        }, ct: ct);
    }

    /// <inheritdoc/>
    public virtual Task<TeamStaffChange> UpdateStaffAsync(
        Guid teamId, Guid callerSubjectId, Guid staffSubjectId, TeamStaffNarrowing narrowing, DateTime now, CancellationToken ct) =>
        AsManagerAsync(teamId, callerSubjectId, TeamStaffChange.NotFound, async (db, token) =>
        {
            await LockAccessOfTeamsAsync(db, [teamId], token);

            var member = await StaffMemberAsync(db, teamId, staffSubjectId, token);
            if (member is null)
                return TeamStaffChange.NotFound;

            var before = NarrowingOf(member);
            if (before == narrowing)
                return TeamStaffChange.Changed;
            if (narrowing.EndsAt != before.EndsAt && MembershipLiveness.HasEnded(narrowing.EndsAt, now))
                return TeamStaffChange.EndPassed;

            member.RoleCeilingSlug = narrowing.RoleCeilingSlug;
            member.LimitTo24Hours = narrowing.LimitTo24Hours;
            member.EndsAt = narrowing.EndsAt;

            if (TeamStaffRules.Widens(before, narrowing))
            {
                var approvedWhereSiteDecides = await db.TeamSiteStaff
                    .Where(s => s.TeamMemberId == member.Id
                        && s.Status == TeamSiteStaffStatus.Approved
                        && !s.TeamTenantAccess!.TeamManagesStaff)
                    .ToListAsync(token);
                foreach (var row in approvedWhereSiteDecides)
                    row.Status = TeamSiteStaffStatus.Pending;
            }

            TeamAudit.StageStaffChanged(db, teamId, staffSubjectId, before, narrowing);
            await db.SaveChangesAsync(token);
            return TeamStaffChange.Changed;
        }, ct);

    private static Task<TeamMemberEntity?> StaffMemberAsync(
        NocturneDbContext db, Guid teamId, Guid subjectId, CancellationToken ct) =>
        db.TeamMembers.FirstOrDefaultAsync(
            m => m.TeamId == teamId && m.SubjectId == subjectId && m.Kind == TeamMemberKind.Staff, ct);

    private static TeamSiteStaffEntity NewSiteStaff(Guid accessId, Guid memberId, bool teamManagesStaff) => new()
    {
        Id = Guid.CreateVersion7(),
        TeamTenantAccessId = accessId,
        TeamMemberId = memberId,
        Status = teamManagesStaff ? TeamSiteStaffStatus.Approved : TeamSiteStaffStatus.Pending,
    };

    private static TeamStaffNarrowing NarrowingOf(TeamMemberEntity member) =>
        new(member.RoleCeilingSlug, member.LimitTo24Hours, Utc(member.EndsAt));

    private static TeamStaffNarrowing NarrowingOf(TeamStaffInviteEntity invite) =>
        new(invite.RoleCeilingSlug, invite.LimitTo24Hours, Utc(invite.StaffEndsAt));

    private static TeamInviteRefusal? StaffEndRefusal(TeamStaffInviteEntity invite, DateTime now) =>
        MembershipLiveness.HasEnded(invite.StaffEndsAt, now) ? TeamInviteRefusal.StaffEndPassed : null;

    private static async Task<IReadOnlyList<TeamStaffMember>> StaffOfAsync(
        NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var staff = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Kind == TeamMemberKind.Staff)
            .OrderBy(m => m.SysCreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.SubjectId,
                DisplayName = m.Subject!.Name,
                m.RoleCeilingSlug,
                m.LimitTo24Hours,
                m.EndsAt,
                Approved = db.TeamSiteStaff.Count(s => s.TeamMemberId == m.Id && s.Status == TeamSiteStaffStatus.Approved),
                Pending = db.TeamSiteStaff.Count(s => s.TeamMemberId == m.Id && s.Status == TeamSiteStaffStatus.Pending),
            })
            .ToListAsync(ct);
        return staff.Select(s => new TeamStaffMember(
            s.SubjectId,
            s.DisplayName,
            new TeamStaffNarrowing(s.RoleCeilingSlug, s.LimitTo24Hours, Utc(s.EndsAt)),
            MembershipLiveness.HasEnded(s.EndsAt, now),
            s.Approved,
            s.Pending)).ToList();
    }

    private static async Task<IReadOnlyList<TeamStaffInviteSummary>> LiveStaffInvitesAsync(
        NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var invites = await LiveLinks(db, db.TeamStaffInvites, teamId, now).AsNoTracking()
            .Where(i => i.StaffEndsAt == null || i.StaffEndsAt > now)
            .OrderBy(i => i.SysCreatedAt)
            .Select(i => new { Invite = i, CreatedByName = i.CreatedBy!.Name })
            .ToListAsync(ct);
        return invites.Select(i => new TeamStaffInviteSummary(
            i.Invite.Id, i.Invite.Label, i.Invite.ExpiresAt, i.Invite.MaxUses, i.Invite.UseCount,
            i.Invite.CreatedBySubjectId, i.CreatedByName, NarrowingOf(i.Invite))).ToList();
    }
}
