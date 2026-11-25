using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// The team writes that remove what a team reason stands on: a site stopping the team's access, the team
/// being deleted, and a staff member being removed or leaving. Each takes its locks and reads the team
/// reasons it ends on the caller's unit, which owns the transaction, and returns them as
/// <see cref="TeamReasonChanges"/>; nothing is saved here.
/// </summary>
/// <remarks>
/// <see cref="MembershipLock"/> gives the order of their locks and why it cannot cycle. A team reason
/// never gives the owner role, so none of these takes a site's owners lock.
/// </remarks>
public partial class TeamRepository
{
    /// <summary>
    /// Locks and reads what stopping the team's access to <paramref name="tenantId"/> ends, on
    /// <paramref name="unit"/>, which must be pinned to the tenant. Null when the team has no access to it.
    /// </summary>
    public static async Task<TeamReasonChanges?> LockAccessStopAsync(
        DbUnitOfWork unit, Guid tenantId, Guid teamId, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockTeamManagersAsync(teamId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var access = await db.TeamTenantAccess
            .Include(a => a.AccessRoles).ThenInclude(r => r.TenantRole)
            .Include(a => a.Team)
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.TeamId == teamId, ct);
        if (access is null)
            return null;

        var changes = await TeamReasonChanges.ReadAsync(unit, [access.Id], subjectId: null, (context, _) =>
        {
            TeamAudit.StageAccessStopped(
                context, access, access.Team!.Name, access.AccessRoles.Select(r => r.TenantRole!.Slug), TeamAudit.StoppedHow);
            context.TeamTenantAccess.Remove(access);
            return Task.CompletedTask;
        }, ct);
        return changes.WithStaffNotices(teamId, [tenantId]);
    }

    /// <summary>
    /// Locks and reads what deleting the team ends, on <paramref name="unit"/>, which must not be pinned
    /// to a tenant. <see cref="TeamDeletion.NotFound"/> when the caller is not a member of it and
    /// <see cref="TeamDeletion.Refused"/> when it may not be deleted now, both with no ending.
    /// </summary>
    public static async Task<(TeamDeletion Outcome, TeamReasonChanges? Ending)> LockTeamDeletionAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockTeamManagersAsync(teamId, ct);

        var team = await db.Teams.FirstOrDefaultAsync(
            t => t.Id == teamId && t.Members.Any(m => m.SubjectId == callerSubjectId), ct);
        if (team is null)
            return (TeamDeletion.NotFound, null);
        if (!await CanDeleteAsync(db, teamId, callerSubjectId, ct))
            return (TeamDeletion.Refused, null);

        var accesses = await AccessOfTeamAcrossSitesAsync(db, teamId, ct);
        var changes = await TeamReasonChanges.ReadAsync(
            unit, accesses.Select(a => a.Access.Id).ToList(), subjectId: null,
            (context, token) => RemoveAsync(context, team, token), ct);
        return (TeamDeletion.Deleted, changes.WithStaffNotices(teamId, accesses.Select(a => a.Access.TenantId)));
    }

    /// <summary>
    /// Locks and reads what removing <paramref name="staffSubjectId"/> from the team ends, on
    /// <paramref name="unit"/>, which must not be pinned to a tenant; the caller's own id means leave.
    /// Null when the subject is not staff of the team, or the caller is neither a manager of it nor that
    /// staff member. Never refused for the last-manager rule.
    /// </summary>
    public static async Task<TeamReasonChanges?> LockStaffRemovalAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, Guid staffSubjectId, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockTeamManagersAsync(teamId, ct);

        if (callerSubjectId != staffSubjectId && !await IsManagerAsync(db, teamId, callerSubjectId, ct))
            return null;

        var member = await StaffMemberAsync(db, teamId, staffSubjectId, ct);
        if (member is null)
            return null;

        var accesses = await AccessOfTeamAcrossSitesAsync(db, teamId, ct);
        var changes = await TeamReasonChanges.ReadAsync(unit, accesses.Select(a => a.Access.Id).ToList(), staffSubjectId, (context, _) =>
        {
            context.TeamMembers.Remove(member);
            TeamAudit.StageStaffRemoved(context, teamId, staffSubjectId);
            return Task.CompletedTask;
        }, ct);
        return changes.WithStaffNotices(teamId, accesses.Select(a => a.Access.TenantId));
    }
}
