using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>A team the instance keeps itself (<see cref="TeamProvision"/>), written by the stages a manager's and an owner's writes take, without their checks.</summary>
public partial class TeamRepository
{
    /// <summary>
    /// Brings the team <paramref name="provision"/> describes into line with it, on <paramref name="unit"/>, which must
    /// not be pinned to a tenant, and saves it: the team, made when it has none; each group, by name; the staff member,
    /// with nothing narrowing them; and at each site an access giving the site's role of the slug, the staff member
    /// approved there, and the site in its groups. Adds only what is missing, so a second run writes nothing. Returns
    /// the team and the team reasons that bring the staff member's memberships of the sites into line
    /// (<see cref="TeamReasonChanges.SyncAsync"/>).
    /// </summary>
    /// <remarks>
    /// The team reaches every person, so its groups narrow nobody's access. Takes the staff member's row, the team's
    /// manager lock and each site's team access lock (<see cref="MembershipLock"/>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">A site has no role of the slug its access gives.</exception>
    public static async Task<(Guid TeamId, TeamReasonChanges Changes)> LockProvisionAsync(
        DbUnitOfWork unit, TeamProvision provision, DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        var subjectId = provision.StaffSubjectId;
        await db.LockSubjectRowSharedAsync(subjectId, ct);

        var team = provision.TeamId is { } existing
            ? await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == existing, ct)
            : null;
        var teamId = team?.Id ?? StageTeam(db, provision.Name, creatorSubjectId: null).Id;
        var teamName = team?.Name ?? provision.Name;
        await db.LockTeamManagersAsync(teamId, ct);
        var sites = provision.Sites.OrderBy(s => s.TenantId).ToList();
        foreach (var site in sites)
            await db.LockTeamAccessAsync(site.TenantId, ct);

        var groups = await db.TeamGroups.AsNoTracking()
            .Where(g => g.TeamId == teamId)
            .ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var name in provision.Groups.Where(name => !groups.ContainsKey(name)))
            groups[name] = StageGroup(db, teamId, name);

        if (await StaffMemberAsync(db, teamId, subjectId, ct) is null)
            await StageStaffAsync(db, teamId, subjectId, new TeamStaffNarrowing(null, false, null), inviteId: null, ct);
        await db.SaveChangesAsync(ct);
        var memberId = (await StaffMemberAsync(db, teamId, subjectId, ct))!.Id;

        foreach (var site in sites)
            await EnsureProvisionedAccessAsync(db, teamId, teamName, memberId, subjectId, site, ct);
        await db.SaveChangesAsync(ct);

        foreach (var site in sites)
        {
            foreach (var groupId in site.Groups.Select(name => groups[name]))
            {
                if (await db.TeamGroupPeople.AnyAsync(p => p.TeamGroupId == groupId && p.TenantId == site.TenantId, ct))
                    continue;
                StageGroupPerson(db, groupId, site.TenantId);
                TeamAudit.StageGroupPerson(
                    db, AuthAuditEventType.TeamGroupPersonAdded, teamId, teamName, groupId, site.TenantId, atSite: false);
            }
        }

        foreach (var groupId in provision.Groups.Select(name => groups[name]))
        {
            if (await db.TeamGroupStaff.AnyAsync(s => s.TeamGroupId == groupId && s.TeamMemberId == memberId, ct))
                continue;
            StageGroupStaff(db, teamId, groupId, memberId);
            TeamAudit.StageGroupStaff(db, AuthAuditEventType.TeamGroupStaffAdded, teamId, teamName, groupId, subjectId, []);
        }

        await db.SaveChangesAsync(ct);
        return (teamId, await TeamReasonChanges.SyncAsync(unit, sites.Select(s => (s.TenantId, subjectId)), now, ct));
    }

    /// <summary>
    /// The demo subject on the team as staff, the first to join; null when it has none. Reads every team's rows, so the
    /// context must not be pinned to a subject.
    /// </summary>
    public static Task<Guid?> FindDemoStaffAsync(NocturneDbContext db, Guid teamId, CancellationToken ct) =>
        db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Kind == TeamMemberKind.Staff && m.Subject!.IsDemoSubject)
            .OrderBy(m => m.SysCreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Guid?)m.SubjectId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Stages the team's access to the site when it has none, which approves the staff member there with the rest of the
    /// team's staff (<see cref="StageAccessAsync"/>), and otherwise the staff member's row there when it is missing.
    /// </summary>
    private static async Task EnsureProvisionedAccessAsync(
        NocturneDbContext db, Guid teamId, string teamName, Guid memberId, Guid subjectId, TeamProvisionSite site,
        CancellationToken ct)
    {
        var access = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.TenantId == site.TenantId)
            .Select(a => new { a.Id, a.StaffRule })
            .FirstOrDefaultAsync(ct);
        if (access is null)
        {
            var roles = await db.TenantRoles.AsNoTracking()
                .Where(r => r.TenantId == site.TenantId && r.Slug == site.RoleSlug)
                .Select(r => new { r.Id, r.Slug })
                .ToListAsync(ct);
            if (roles.Count == 0)
                throw new InvalidOperationException($"Site {site.TenantId} has no {site.RoleSlug} role to give the team.");

            var grant = new TeamAccessGrant(
                site.TenantId, [.. roles.Select(r => r.Id)], site.LimitTo24Hours, null, TeamStaffRule.TeamDecides);
            await StageAccessAsync(
                db, teamId, teamName, grant, [.. roles.Select(r => (r.Id, r.Slug))], approverSubjectId: null, linkId: null, ct);
            return;
        }

        if (!await db.TeamSiteStaff.AnyAsync(s => s.TeamTenantAccessId == access.Id && s.TeamMemberId == memberId, ct))
            db.TeamSiteStaff.Add(await NewSiteStaffAsync(db, access.Id, memberId, subjectId, access.StaffRule, ct));
    }
}
