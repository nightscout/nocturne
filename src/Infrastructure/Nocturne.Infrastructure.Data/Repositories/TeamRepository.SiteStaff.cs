using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// An owner of a site deciding which of a team's staff reach it: approving or declining one, setting
/// who decides (<see cref="TeamStaffRule"/>), and stopping one person through every team
/// (<see cref="TeamSiteBlockEntity"/>). Each is decided under the locks, by the owner check every
/// owner's decision about a team shares (<see cref="DirectOwnerAsync"/>), and returns the team reasons that
/// bring the staff it changed into line with the rows (<see cref="TeamReasonChanges.SyncAsync"/>).
/// </summary>
/// <remarks>
/// A declined row stays declined whatever the team does, since nothing but these writes sets a row's
/// status from declined, and its memory (<see cref="TeamSiteDeclineEntity"/>) declines the row a rejoin
/// makes. Trusting the whole team leaves every row and decline as it is: what a row gives under each rule
/// is <see cref="TeamReasons.GivesReason"/>.
/// </remarks>
public partial class TeamRepository
{
    /// <summary>
    /// Sets the <c>team_site_staff</c> row of <paramref name="staffSubjectId"/> on the team's access to
    /// <paramref name="tenantId"/> to <paramref name="status"/>, as <paramref name="callerSubjectId"/>, on
    /// <paramref name="unit"/>, pinned to the tenant, and saves it. Approving lifts the decline, and
    /// declining records one, so a rejoin is declined too. A row already at the status changes nothing.
    /// </summary>
    /// <remarks>
    /// Takes the staff member's row, the site's owners lock and its team access lock, in the order
    /// <see cref="MembershipLock"/> states: the access lock holds the access and the staff member's row
    /// still against a stop, a removal and an edit, and the owners lock holds the caller's ownership.
    /// <see cref="OwnerDecision.TeamTrusted"/>, writing nothing, while the site trusts the whole team, read
    /// under the team access lock the rule's writer takes.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="status"/> is neither approved nor declined.</exception>
    public static async Task<(OwnerDecision Decision, TeamReasonChanges? Changes)?> LockStaffDecisionAsync(
        DbUnitOfWork unit, Guid tenantId, Guid teamId, Guid callerSubjectId, Guid staffSubjectId, string status,
        DateTime now, CancellationToken ct)
    {
        if (status is not (TeamSiteStaffStatus.Approved or TeamSiteStaffStatus.Declined))
            throw new ArgumentException($"An owner decides {TeamSiteStaffStatus.Approved} or {TeamSiteStaffStatus.Declined}, not {status}.", nameof(status));

        var db = unit.Context;
        await db.LockSubjectRowSharedAsync(staffSubjectId, ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var (owner, _) = await DirectOwnerAsync(db, tenantId, callerSubjectId, now, ct);
        if (owner != OwnerDecision.Decided)
            return (owner, null);

        var access = await AccessOfSiteAsync(db, tenantId, teamId, ct);
        var row = access is null
            ? null
            : await db.TeamSiteStaff.FirstOrDefaultAsync(s => s.TeamTenantAccessId == access.Id
                && s.TeamMember!.SubjectId == staffSubjectId
                && s.TeamMember.Kind == TeamMemberKind.Staff, ct);
        if (access is null || row is null)
            return (OwnerDecision.NotFound, null);
        if (access.StaffRule == TeamStaffRule.TeamTrusted)
            return (OwnerDecision.TeamTrusted, null);
        if (row.Status == status)
            return (OwnerDecision.Decided, null);

        row.Status = status;
        var declines = db.TeamSiteDeclines.Where(d => d.TeamTenantAccessId == access.Id && d.SubjectId == staffSubjectId);
        if (status == TeamSiteStaffStatus.Approved)
        {
            db.TeamSiteDeclines.RemoveRange(await declines.ToListAsync(ct));
        }
        else if (!await declines.AnyAsync(ct))
        {
            db.TeamSiteDeclines.Add(new TeamSiteDeclineEntity
            {
                Id = Guid.CreateVersion7(),
                TeamTenantAccessId = access.Id,
                SubjectId = staffSubjectId,
            });
        }

        TeamAudit.StageStaffDecided(
            db,
            status == TeamSiteStaffStatus.Approved ? AuthAuditEventType.TeamStaffApproved : AuthAuditEventType.TeamStaffDeclined,
            access,
            await TeamNameAsync(db, teamId, ct),
            staffSubjectId);
        await db.SaveChangesAsync(ct);
        return (OwnerDecision.Decided, (await TeamReasonChanges.SyncAsync(unit, [(tenantId, staffSubjectId)], now, ct))
            .WithStaffNotices(teamId, [tenantId]));
    }

    /// <summary>
    /// Sets who decides which of the team's staff reach <paramref name="tenantId"/> to <paramref name="staffRule"/>,
    /// as <paramref name="callerSubjectId"/>, on <paramref name="unit"/>, pinned to the tenant, and saves it. A
    /// rule already in force changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To <see cref="TeamStaffRule.TeamDecides"/>, from either other rule, approves every pending staff member
    /// in the same commit, one approval row each, and leaves the declined declined, since under that rule nobody
    /// waits. To <see cref="TeamStaffRule.OwnerApproves"/> changes only the rule, so staff already approved keep
    /// their access. To <see cref="TeamStaffRule.TeamTrusted"/> changes no row and no decline. To or from it
    /// syncs every staff member of the team at the site, so the pending and the declined gain or lose the
    /// reason in the same commit.
    /// </para>
    /// <para>
    /// Takes the rows of the team's staff, the site's owners lock, the team's manager lock and the site's
    /// team access lock (<see cref="MembershipLock"/>). Null, writing nothing, when a staff member joined
    /// between the read of the staff and those locks: the caller runs it again.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="staffRule"/> is not a <see cref="TeamStaffRule"/>.</exception>
    public static async Task<(OwnerDecision Decision, TeamReasonChanges? Changes)?> LockStaffRuleAsync(
        DbUnitOfWork unit, Guid tenantId, Guid teamId, Guid callerSubjectId, string staffRule, DateTime now,
        CancellationToken ct)
    {
        if (!TeamStaffRule.IsKnown(staffRule))
            throw new ArgumentException($"{staffRule} is not a staff rule.", nameof(staffRule));

        var db = unit.Context;
        var locked = (await StaffSubjectsAsync(db, teamId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var staffSubjects = await StaffSubjectsAsync(db, teamId, ct);
        if (!locked.IsSupersetOf(staffSubjects))
            return null;

        var (owner, _) = await DirectOwnerAsync(db, tenantId, callerSubjectId, now, ct);
        if (owner != OwnerDecision.Decided)
            return (owner, null);

        if (await AccessOfSiteAsync(db, tenantId, teamId, ct) is not { } access)
            return (OwnerDecision.NotFound, null);
        var before = access.StaffRule;
        if (before == staffRule)
            return (OwnerDecision.Decided, null);

        access.StaffRule = staffRule;
        var teamName = await TeamNameAsync(db, teamId, ct);
        TeamAudit.StageStaffRuleChanged(db, access, teamName);

        var approved = new List<Guid>();
        if (staffRule == TeamStaffRule.TeamDecides)
        {
            var pending = await db.TeamSiteStaff
                .Where(s => s.TeamTenantAccessId == access.Id && s.Status == TeamSiteStaffStatus.Pending)
                .Select(s => new { Row = s, s.TeamMember!.SubjectId })
                .ToListAsync(ct);
            foreach (var staff in pending.OrderBy(p => p.SubjectId))
            {
                staff.Row.Status = TeamSiteStaffStatus.Approved;
                TeamAudit.StageStaffDecided(db, AuthAuditEventType.TeamStaffApproved, access, teamName, staff.SubjectId);
                approved.Add(staff.SubjectId);
            }
        }

        await db.SaveChangesAsync(ct);
        var synced = before == TeamStaffRule.TeamTrusted || staffRule == TeamStaffRule.TeamTrusted ? staffSubjects : approved;
        return (OwnerDecision.Decided, (await TeamReasonChanges.SyncAsync(
            unit, synced.Select(subjectId => (tenantId, subjectId)), now, ct))
            .WithStaffNotices(teamId, [tenantId]));
    }

    /// <summary>
    /// Stops <paramref name="subjectId"/> reaching <paramref name="tenantId"/> through any team, when
    /// <paramref name="blocked"/>, or removes the stop, as <paramref name="callerSubjectId"/>, on
    /// <paramref name="unit"/>, pinned to the tenant, and saves it. A stop already set, or one not there to
    /// remove, changes nothing. Setting one needs the subject to be staff of a team with access to the site,
    /// so an owner cannot stop an arbitrary account; removing one needs only the stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allowed under every staff rule: a stop holds through a team the site trusts as a whole
    /// (<see cref="TeamReasons.GivesReason"/>). It changes no staff row, no decline and no rule, so removing it
    /// gives back exactly what those give. A direct reason is not touched.
    /// </para>
    /// <para>
    /// Takes the rows of the subject and the caller, whom the stop names, the site's owners lock and its team
    /// access lock (<see cref="MembershipLock"/>). It and the subject joining another team that follows the
    /// site are ordered by the site's team access lock, which each takes first, and again by the membership
    /// lock their syncs take before they read; neither excludes the other at the subject's row
    /// (<c>KEY SHARE</c>). So the join either commits first and its reason is removed here, or reads the stop
    /// and gives none. The changes owe the owners a recount of every team the subject is staff of at the
    /// site, and no notice of their own.
    /// </para>
    /// </remarks>
    public static async Task<(OwnerDecision Decision, TeamReasonChanges? Changes)?> LockStaffBlockAsync(
        DbUnitOfWork unit, Guid tenantId, Guid callerSubjectId, Guid subjectId, bool blocked, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockSubjectRowsSharedAsync([callerSubjectId, subjectId], ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var (owner, _) = await DirectOwnerAsync(db, tenantId, callerSubjectId, now, ct);
        if (owner != OwnerDecision.Decided)
            return (owner, null);

        var block = await db.TeamSiteBlocks.FirstOrDefaultAsync(b => b.TenantId == tenantId && b.SubjectId == subjectId, ct);
        if ((block is not null) == blocked)
            return (OwnerDecision.Decided, null);

        var teams = await db.TeamSiteStaff.AsNoTracking()
            .Where(s => s.TeamTenantAccess!.TenantId == tenantId
                && s.TeamMember!.SubjectId == subjectId
                && s.TeamMember.Kind == TeamMemberKind.Staff)
            .Select(s => s.TeamTenantAccess!.TeamId)
            .ToListAsync(ct);
        if (blocked && teams.Count == 0)
            return (OwnerDecision.NotFound, null);

        if (blocked)
        {
            db.TeamSiteBlocks.Add(new TeamSiteBlockEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SubjectId = subjectId,
                CreatedBySubjectId = callerSubjectId,
            });
        }
        else
        {
            db.TeamSiteBlocks.Remove(block!);
        }

        TeamAudit.StageStaffBlock(
            db, blocked ? AuthAuditEventType.TeamStaffBlocked : AuthAuditEventType.TeamStaffUnblocked, tenantId, subjectId);
        await db.SaveChangesAsync(ct);
        return (OwnerDecision.Decided, (await TeamReasonChanges.SyncAsync(unit, [(tenantId, subjectId)], now, ct))
            .WithRecounts(teams.Select(team => new TeamSiteKey(tenantId, team))));
    }

    private static Task<TeamTenantAccessEntity?> AccessOfSiteAsync(
        NocturneDbContext db, Guid tenantId, Guid teamId, CancellationToken ct) =>
        db.TeamTenantAccess.FirstOrDefaultAsync(a => a.TeamId == teamId && a.TenantId == tenantId, ct);

    private static Task<string> TeamNameAsync(NocturneDbContext db, Guid teamId, CancellationToken ct) =>
        db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstAsync(ct);
}
