using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// A team's staff: the invites a manager makes, the narrowing a manager sets, and the
/// <c>team_site_staff</c> row each staff member has for each site the team follows. A join and an edit
/// return the team reasons that bring the staff member's memberships into line with those rows
/// (<see cref="TeamReasonChanges"/>); removing a staff member removes the reasons it ends
/// (<see cref="LockStaffRemovalAsync"/>).
/// </summary>
/// <remarks>
/// Every write here holds the team's manager lock, which an approval of the team's access holds too, so
/// a join and an approval each see the other's rows. A join also takes the access locks of every site
/// the team follows (<see cref="LockAccessOfTeamsAsync"/>), in the order <see cref="MembershipLock"/>
/// gives every write that takes them, after the staff member's subject row. So a join either reads the
/// access before a stop commits and has its row and reason removed by the stop, or waits and reads it
/// gone. An edit takes the same locks, so the rows it loads cannot be removed by a stop before it saves.
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

    /// <summary>
    /// Makes <paramref name="subjectId"/> staff of the invite's team, on <paramref name="unit"/>, which must
    /// not be pinned to a tenant: with what the invite gives, one use of the invite taken, and a
    /// <c>team_site_staff</c> row for each site the team has access to, saved. Returns with it the team
    /// reasons that bring the subject's memberships of those sites into line
    /// (<see cref="TeamReasonChanges.SyncAsync"/>), none when it is refused. A subject whose account was
    /// deleted first is <see cref="TeamInviteRefusal.NotFound"/>. A subject already a member of
    /// either kind changes nothing and is told so. A manager token is <see cref="TeamInviteRefusal.NotFound"/>
    /// here, as a staff token is to <see cref="AcceptInviteAsync"/>.
    /// </summary>
    public static async Task<(TeamInviteAcceptance Acceptance, TeamReasonChanges? Changes)> LockStaffJoinAsync(
        DbUnitOfWork unit, string tokenHash, Guid subjectId, DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockSubjectRowSharedAsync(subjectId, ct);
        if (!await db.Subjects.AnyAsync(s => s.Id == subjectId, ct))
            return (new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound), null);

        var teamId = await db.TeamStaffInvites.AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => (Guid?)i.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is not { } id)
            return (new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound), null);

        await db.LockTeamManagersAsync(id, ct);
        await LockAccessOfTeamsAsync(db, [id], ct);

        var invite = await db.TeamStaffInvites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        if (invite is null)
            return (new TeamInviteAcceptance(null, TeamInviteRefusal.NotFound), null);
        if (await RefusalAsync(db, invite, now, ct) is { } refusal)
            return (new TeamInviteAcceptance(id, refusal), null);
        if (StaffEndRefusal(invite, now) is { } ended)
            return (new TeamInviteAcceptance(id, ended), null);
        if (await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.SubjectId == subjectId, ct))
            return (new TeamInviteAcceptance(id, TeamInviteRefusal.AlreadyMember), null);

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
            .Select(a => new { a.Id, a.TenantId, a.TeamManagesStaff })
            .ToListAsync(ct);
        foreach (var access in accesses)
            db.TeamSiteStaff.Add(await NewSiteStaffAsync(db, access.Id, member.Id, subjectId, access.TeamManagesStaff, ct));

        TeamAudit.StageStaffAdded(db, id, subjectId, invite.Id);
        await db.SaveChangesAsync(ct);
        return (new TeamInviteAcceptance(id, null), (await TeamReasonChanges.SyncAsync(
            unit, accesses.Select(a => (a.TenantId, subjectId)), now, ct)).WithStaffNotices(id, accesses.Select(a => a.TenantId)));
    }

    /// <summary>
    /// Sets what narrows the staff member <paramref name="staffSubjectId"/>, on <paramref name="unit"/>, which
    /// must not be pinned to a tenant, and saves it. An edit that gives them more than they held
    /// (<see cref="TeamStaffRules.Widens"/>) returns every approval of theirs that the site, not the team,
    /// decides to pending. Returns with it the team reasons that bring the staff member's memberships of
    /// the team's sites into line (<see cref="TeamReasonChanges.SyncAsync"/>), none when nothing changed.
    /// <see cref="TeamStaffChange.NotFound"/> when the caller is not a manager of the team or the subject
    /// is not its staff, and <see cref="TeamStaffChange.EndPassed"/> when the end at <paramref name="now"/>
    /// has passed and is not the end they already have, so a manager can still narrow a staff member whose
    /// end has passed without moving it.
    /// </summary>
    public static async Task<(TeamStaffChange Change, TeamReasonChanges? Changes)> LockStaffUpdateAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, Guid staffSubjectId, TeamStaffNarrowing narrowing,
        DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockSubjectRowSharedAsync(staffSubjectId, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        if (!await IsManagerAsync(db, teamId, callerSubjectId, ct))
            return (TeamStaffChange.NotFound, null);

        await LockAccessOfTeamsAsync(db, [teamId], ct);

        var member = await StaffMemberAsync(db, teamId, staffSubjectId, ct);
        if (member is null)
            return (TeamStaffChange.NotFound, null);

        var before = NarrowingOf(member);
        if (before == narrowing)
            return (TeamStaffChange.Changed, null);
        if (narrowing.EndsAt != before.EndsAt && MembershipLiveness.HasEnded(narrowing.EndsAt, now))
            return (TeamStaffChange.EndPassed, null);

        member.RoleCeilingSlug = narrowing.RoleCeilingSlug;
        member.LimitTo24Hours = narrowing.LimitTo24Hours;
        member.EndsAt = narrowing.EndsAt;

        var sentBack = new List<Guid>();
        if (TeamStaffRules.Widens(before, narrowing))
        {
            var approvedWhereSiteDecides = await db.TeamSiteStaff
                .Where(s => s.TeamMemberId == member.Id
                    && s.Status == TeamSiteStaffStatus.Approved
                    && !s.TeamTenantAccess!.TeamManagesStaff)
                .Select(s => new { Row = s, s.TeamTenantAccess!.TenantId })
                .ToListAsync(ct);
            foreach (var approved in approvedWhereSiteDecides)
            {
                approved.Row.Status = TeamSiteStaffStatus.Pending;
                sentBack.Add(approved.TenantId);
            }
        }

        TeamAudit.StageStaffChanged(db, teamId, staffSubjectId, before, narrowing);
        await db.SaveChangesAsync(ct);

        var sites = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId)
            .Select(a => a.TenantId)
            .ToListAsync(ct);
        return (TeamStaffChange.Changed, (await TeamReasonChanges.SyncAsync(
            unit, sites.Select(site => (site, staffSubjectId)), now, ct)).WithStaffNotices(teamId, sentBack));
    }

    private static Task<TeamMemberEntity?> StaffMemberAsync(
        NocturneDbContext db, Guid teamId, Guid subjectId, CancellationToken ct) =>
        db.TeamMembers.FirstOrDefaultAsync(
            m => m.TeamId == teamId && m.SubjectId == subjectId && m.Kind == TeamMemberKind.Staff, ct);

    /// <summary>
    /// The <c>team_site_staff</c> row of a staff member at a site: declined when an owner of the site
    /// declined the subject on this access before (<see cref="TeamSiteDeclineEntity"/>), else approved when
    /// the team manages its staff there and pending when it does not.
    /// </summary>
    private static async Task<TeamSiteStaffEntity> NewSiteStaffAsync(
        NocturneDbContext db, Guid accessId, Guid memberId, Guid subjectId, bool teamManagesStaff, CancellationToken ct) => new()
    {
        Id = Guid.CreateVersion7(),
        TeamTenantAccessId = accessId,
        TeamMemberId = memberId,
        Status = await db.TeamSiteDeclines.AnyAsync(d => d.TeamTenantAccessId == accessId && d.SubjectId == subjectId, ct)
            ? TeamSiteStaffStatus.Declined
            : teamManagesStaff ? TeamSiteStaffStatus.Approved : TeamSiteStaffStatus.Pending,
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
