using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// An owner of a site deciding which of a team's staff reach it: approving or declining one, and turning
/// "the team manages its own staff" on or off. Each is decided under the locks, by the owner check every
/// owner's decision about a team shares (<see cref="DirectOwnerAsync"/>), and returns the team reasons that
/// bring the staff it changed into line with the rows (<see cref="TeamReasonChanges.SyncAsync"/>).
/// </summary>
/// <remarks>
/// A declined row stays declined whatever the team does, since nothing but these writes sets a row's
/// status from declined, and its memory (<see cref="TeamSiteDeclineEntity"/>) declines the row a rejoin
/// makes.
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
    /// Turns "the team manages its own staff" on the team's access to <paramref name="tenantId"/> to
    /// <paramref name="teamManagesStaff"/>, as <paramref name="callerSubjectId"/>, on <paramref name="unit"/>,
    /// pinned to the tenant, and saves it. Off changes only the switch, so staff already approved keep
    /// their access. On approves every pending staff member in the same commit, one approval row each,
    /// and leaves the declined declined. A switch already at the value changes nothing.
    /// </summary>
    /// <remarks>
    /// Takes the rows of the team's staff, the site's owners lock, the team's manager lock and the site's
    /// team access lock (<see cref="MembershipLock"/>). Null, writing nothing, when a staff member joined
    /// between the read of the staff and those locks: the caller runs it again.
    /// </remarks>
    public static async Task<(OwnerDecision Decision, TeamReasonChanges? Changes)?> LockStaffRuleAsync(
        DbUnitOfWork unit, Guid tenantId, Guid teamId, Guid callerSubjectId, bool teamManagesStaff, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        var locked = (await StaffSubjectsAsync(db, teamId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        if (!locked.IsSupersetOf(await StaffSubjectsAsync(db, teamId, ct)))
            return null;

        var (owner, _) = await DirectOwnerAsync(db, tenantId, callerSubjectId, now, ct);
        if (owner != OwnerDecision.Decided)
            return (owner, null);

        if (await AccessOfSiteAsync(db, tenantId, teamId, ct) is not { } access)
            return (OwnerDecision.NotFound, null);
        if (access.TeamManagesStaff == teamManagesStaff)
            return (OwnerDecision.Decided, null);

        access.TeamManagesStaff = teamManagesStaff;
        var teamName = await TeamNameAsync(db, teamId, ct);
        TeamAudit.StageStaffRuleChanged(db, access, teamName);

        var approved = new List<Guid>();
        if (teamManagesStaff)
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
        return (OwnerDecision.Decided, (await TeamReasonChanges.SyncAsync(
            unit, approved.Select(subjectId => (tenantId, subjectId)), now, ct))
            .WithStaffNotices(teamId, [tenantId]));
    }

    private static Task<TeamTenantAccessEntity?> AccessOfSiteAsync(
        NocturneDbContext db, Guid tenantId, Guid teamId, CancellationToken ct) =>
        db.TeamTenantAccess.FirstOrDefaultAsync(a => a.TeamId == teamId && a.TenantId == tenantId, ct);

    private static Task<string> TeamNameAsync(NocturneDbContext db, Guid teamId, CancellationToken ct) =>
        db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstAsync(ct);
}
