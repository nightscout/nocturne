using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// A team's groups (<see cref="TeamGroupEntity"/>) and which people its staff reach
/// (<see cref="TeamStaffReach"/>), each changed by a manager of the team under its manager lock, by the
/// manager check every manager's write shares. A change that can change who reaches a site returns the team
/// reasons that bring the staff and sites it touches into line (<see cref="TeamReasonChanges.SyncAsync"/>),
/// owing the owners a recount of the waiting staff there and no notice of its own.
/// </summary>
/// <remarks>
/// <para>
/// While the team reaches every person, a group changes nobody's access, so a change to one syncs nothing and
/// touches no membership. Making a group and renaming one never do.
/// </para>
/// <para>
/// Each write takes its locks as <see cref="MembershipLock"/> lists them, so a staff member's join and edit, a
/// site's block and the switch of who reaches whom each either commit before the group write reads or read what
/// it committed. A write that finds under its locks a staff member whose row it did not lock first writes
/// nothing, and runs again.
/// </para>
/// </remarks>
public partial class TeamRepository
{
    /// <inheritdoc/>
    public virtual Task<TeamGroupCreation> CreateGroupAsync(Guid teamId, Guid callerSubjectId, string name, CancellationToken ct) =>
        AsManagerAsync(teamId, callerSubjectId, new TeamGroupCreation(TeamGroupChange.NotFound, null), async (db, token) =>
        {
            if (await NameTakenAsync(db, teamId, null, name, token))
                return new TeamGroupCreation(TeamGroupChange.NameTaken, null);

            var groupId = StageGroup(db, teamId, name);
            await db.SaveChangesAsync(token);
            return new TeamGroupCreation(TeamGroupChange.Changed, groupId);
        }, ct);

    /// <inheritdoc/>
    public virtual Task<TeamGroupChange> RenameGroupAsync(
        Guid teamId, Guid callerSubjectId, Guid groupId, string name, CancellationToken ct) =>
        AsManagerAsync(teamId, callerSubjectId, TeamGroupChange.NotFound, async (db, token) =>
        {
            var group = await db.TeamGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.TeamId == teamId, token);
            if (group is null)
                return TeamGroupChange.NotFound;
            if (group.Name == name)
                return TeamGroupChange.Changed;
            if (await NameTakenAsync(db, teamId, groupId, name, token))
                return TeamGroupChange.NameTaken;

            TeamAudit.StageGroupRenamed(db, teamId, groupId, group.Name, name);
            group.Name = name;
            await db.SaveChangesAsync(token);
            return TeamGroupChange.Changed;
        }, ct);

    /// <summary>
    /// Puts the site <paramref name="tenantId"/> in the team's group, when <paramref name="add"/>, or takes it
    /// out, as <paramref name="callerSubjectId"/>, on <paramref name="unit"/>, which must not be pinned to a
    /// tenant, and saves it. Putting in needs a site the team follows with access that has not ended at
    /// <paramref name="now"/>; a site already there, or one not there to take out, changes nothing.
    /// </summary>
    public static async Task<(TeamGroupChange Change, TeamReasonChanges? Changes)?> LockGroupPersonAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, Guid groupId, Guid tenantId, bool add, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        if (await LockGroupAsync(db, teamId, callerSubjectId, groupId, ct) is not { } locked)
            return null;
        if (locked is not { Team: { } team, Group: not null })
            return (TeamGroupChange.NotFound, null);

        var access = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.TenantId == tenantId)
            .Select(a => new { a.AccessEndsAt })
            .FirstOrDefaultAsync(ct);
        if (add && (access is null || MembershipLiveness.HasEnded(access.AccessEndsAt, now)))
            return (TeamGroupChange.NotFound, null);

        var place = await db.TeamGroupPeople.FirstOrDefaultAsync(p => p.TeamGroupId == groupId && p.TenantId == tenantId, ct);
        if ((place is not null) == add)
            return (TeamGroupChange.Changed, null);

        await db.LockTeamAccessAsync(tenantId, ct);
        if (add)
            StageGroupPerson(db, groupId, tenantId);
        else
            db.TeamGroupPeople.Remove(place!);

        var changesAccess = team.StaffReach != TeamStaffReach.EveryPerson && access is not null;
        TeamAudit.StageGroupPerson(
            db, add ? AuthAuditEventType.TeamGroupPersonAdded : AuthAuditEventType.TeamGroupPersonRemoved,
            teamId, team.Name, groupId, tenantId, atSite: changesAccess);
        await db.SaveChangesAsync(ct);
        return (TeamGroupChange.Changed, changesAccess
            ? await SyncGroupAsync(unit, teamId, [tenantId], locked.Staff, now, ct)
            : null);
    }

    /// <summary>
    /// Puts the staff member <paramref name="staffSubjectId"/> in the team's group, when <paramref name="add"/>,
    /// or takes them out, as <paramref name="callerSubjectId"/>, on <paramref name="unit"/>, which must not be
    /// pinned to a tenant, and saves it. Putting in needs current staff of the team, whose end has not passed
    /// at <paramref name="now"/>; taking out needs staff of the team. One already there, or not there to take
    /// out, changes nothing.
    /// </summary>
    public static async Task<(TeamGroupChange Change, TeamReasonChanges? Changes)?> LockGroupStaffAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, Guid groupId, Guid staffSubjectId, bool add, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockSubjectRowSharedAsync(staffSubjectId, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        if (!await IsManagerAsync(db, teamId, callerSubjectId, ct))
            return (TeamGroupChange.NotFound, null);

        var team = await db.Teams.AsNoTracking().Where(t => t.Id == teamId).Select(t => new { t.Name, t.StaffReach }).FirstAsync(ct);
        var member = await StaffMemberAsync(db, teamId, staffSubjectId, ct);
        if (!await db.TeamGroups.AnyAsync(g => g.Id == groupId && g.TeamId == teamId, ct)
            || member is null
            || (add && MembershipLiveness.HasEnded(member.EndsAt, now)))
            return (TeamGroupChange.NotFound, null);

        var place = await db.TeamGroupStaff.FirstOrDefaultAsync(s => s.TeamGroupId == groupId && s.TeamMemberId == member.Id, ct);
        if ((place is not null) == add)
            return (TeamGroupChange.Changed, null);

        var sites = await SitesOfGroupAsync(db, teamId, groupId, ct);
        foreach (var site in sites)
            await db.LockTeamAccessAsync(site, ct);

        if (add)
            StageGroupStaff(db, teamId, groupId, member.Id);
        else
            db.TeamGroupStaff.Remove(place!);

        var narrows = team.StaffReach != TeamStaffReach.EveryPerson;
        TeamAudit.StageGroupStaff(
            db, add ? AuthAuditEventType.TeamGroupStaffAdded : AuthAuditEventType.TeamGroupStaffRemoved,
            teamId, team.Name, groupId, staffSubjectId, narrows ? sites : []);
        await db.SaveChangesAsync(ct);
        return (TeamGroupChange.Changed, narrows
            ? await SyncGroupAsync(unit, teamId, sites, [staffSubjectId], now, ct)
            : null);
    }

    /// <summary>
    /// Deletes the team's group with its people and staff, as <paramref name="callerSubjectId"/>, on
    /// <paramref name="unit"/>, which must not be pinned to a tenant, and saves it. While the team narrows to
    /// groups, the staff lose the access the group alone gave them.
    /// </summary>
    public static async Task<(TeamGroupChange Change, TeamReasonChanges? Changes)?> LockGroupDeletionAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, Guid groupId, DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        if (await LockGroupAsync(db, teamId, callerSubjectId, groupId, ct) is not { } locked)
            return null;
        if (locked is not { Team: { } team, Group: { } group })
            return (TeamGroupChange.NotFound, null);

        var sites = await SitesOfGroupAsync(db, teamId, groupId, ct);
        foreach (var site in sites)
            await db.LockTeamAccessAsync(site, ct);

        var narrows = team.StaffReach != TeamStaffReach.EveryPerson;
        TeamAudit.StageGroupDeleted(db, teamId, team.Name, groupId, group.Name, narrows ? sites : []);
        db.TeamGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        return (TeamGroupChange.Changed, narrows
            ? await SyncGroupAsync(unit, teamId, sites, locked.Staff, now, ct)
            : null);
    }

    /// <summary>
    /// Sets which people the team's staff reach to <paramref name="staffReach"/>, as
    /// <paramref name="callerSubjectId"/>, on <paramref name="unit"/>, which must not be pinned to a tenant, and
    /// saves it, syncing every staff member at every site the team follows. A value already in force changes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Takes its locks as <see cref="MembershipLock"/> lists them. Null, writing nothing, when a staff member
    /// joined between the read of the staff and those locks: the caller runs it again.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="staffReach"/> is not a <see cref="TeamStaffReach"/>.</exception>
    public static async Task<(TeamGroupChange Change, TeamReasonChanges? Changes)?> LockStaffReachAsync(
        DbUnitOfWork unit, Guid teamId, Guid callerSubjectId, string staffReach, DateTime now, CancellationToken ct)
    {
        if (!TeamStaffReach.IsKnown(staffReach))
            throw new ArgumentException($"{staffReach} is not a staff reach.", nameof(staffReach));

        var db = unit.Context;
        var locked = (await StaffSubjectsAsync(db, teamId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        if (!await IsManagerAsync(db, teamId, callerSubjectId, ct))
            return (TeamGroupChange.NotFound, null);

        var staff = await StaffSubjectsAsync(db, teamId, ct);
        if (!locked.IsSupersetOf(staff))
            return null;

        var team = await db.Teams.FirstAsync(t => t.Id == teamId, ct);
        if (team.StaffReach == staffReach)
            return (TeamGroupChange.Changed, null);

        await LockAccessOfTeamsAsync(db, [teamId], ct);
        var sites = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId)
            .Select(a => a.TenantId)
            .OrderBy(site => site)
            .ToListAsync(ct);

        team.StaffReach = staffReach;
        TeamAudit.StageStaffReachChanged(db, teamId, team.Name, staffReach, sites);
        await db.SaveChangesAsync(ct);
        return (TeamGroupChange.Changed, await SyncGroupAsync(unit, teamId, sites, staff, now, ct));
    }

    /// <summary>Stages an empty group named <paramref name="name"/> in the team, and its row in the team's history. The caller saves.</summary>
    private static Guid StageGroup(NocturneDbContext db, Guid teamId, string name)
    {
        var group = new TeamGroupEntity { Id = Guid.CreateVersion7(), TeamId = teamId, Name = name };
        db.TeamGroups.Add(group);
        TeamAudit.StageGroupCreated(db, teamId, group.Id, name);
        return group.Id;
    }

    private static void StageGroupPerson(NocturneDbContext db, Guid groupId, Guid tenantId) =>
        db.TeamGroupPeople.Add(new TeamGroupPersonEntity { Id = Guid.CreateVersion7(), TeamGroupId = groupId, TenantId = tenantId });

    private static void StageGroupStaff(NocturneDbContext db, Guid teamId, Guid groupId, Guid teamMemberId) =>
        db.TeamGroupStaff.Add(new TeamGroupStaffEntity
        {
            Id = Guid.CreateVersion7(), TeamGroupId = groupId, TeamId = teamId, TeamMemberId = teamMemberId,
        });

    /// <summary>A group read under its locks; <c>Group</c> is null when the caller may not change it, and then <c>Team</c> may be too.</summary>
    private sealed record LockedGroup(TeamEntity? Team, TeamGroupEntity? Group, IReadOnlyList<Guid> Staff);

    /// <summary>
    /// Takes the rows of the group's staff and the team's manager lock, checks the caller manages the team and
    /// reads the team, the group and its staff under them. Null when a staff member was put in the group
    /// between the first read and the locks; a group of none when the caller does not manage the team or it has
    /// no such group.
    /// </summary>
    private static async Task<LockedGroup?> LockGroupAsync(
        NocturneDbContext db, Guid teamId, Guid callerSubjectId, Guid groupId, CancellationToken ct)
    {
        var locked = (await StaffOfGroupAsync(db, groupId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTeamManagersAsync(teamId, ct);

        if (!await IsManagerAsync(db, teamId, callerSubjectId, ct))
            return new LockedGroup(null, null, []);

        var team = await db.Teams.AsNoTracking().FirstAsync(t => t.Id == teamId, ct);
        var group = await db.TeamGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.TeamId == teamId, ct);
        if (group is null)
            return new LockedGroup(team, null, []);

        var staff = await StaffOfGroupAsync(db, groupId, ct);
        return locked.IsSupersetOf(staff) ? new LockedGroup(team, group, staff) : null;
    }

    private static Task<List<Guid>> StaffOfGroupAsync(NocturneDbContext db, Guid groupId, CancellationToken ct) =>
        db.TeamGroupStaff.AsNoTracking()
            .Where(s => s.TeamGroupId == groupId)
            .Select(s => s.TeamMember!.SubjectId)
            .ToListAsync(ct);

    /// <summary>The sites in the group that the team follows, in ascending tenant id.</summary>
    private static Task<List<Guid>> SitesOfGroupAsync(NocturneDbContext db, Guid teamId, Guid groupId, CancellationToken ct) =>
        db.TeamGroupPeople.AsNoTracking()
            .Where(p => p.TeamGroupId == groupId && db.TeamTenantAccess.Any(a => a.TeamId == teamId && a.TenantId == p.TenantId))
            .Select(p => p.TenantId)
            .OrderBy(site => site)
            .ToListAsync(ct);

    /// <summary>
    /// The team reasons of each of <paramref name="staff"/> at each of <paramref name="sites"/>, owing a recount
    /// of the team's waiting staff at each site.
    /// </summary>
    private static async Task<TeamReasonChanges> SyncGroupAsync(
        DbUnitOfWork unit, Guid teamId, IReadOnlyCollection<Guid> sites, IReadOnlyCollection<Guid> staff, DateTime now,
        CancellationToken ct) =>
        (await TeamReasonChanges.SyncAsync(unit, sites.SelectMany(site => staff.Select(subject => (site, subject))), now, ct))
            .WithRecounts(sites.Select(site => new TeamSiteKey(site, teamId)));

    private static Task<bool> NameTakenAsync(NocturneDbContext db, Guid teamId, Guid? groupId, string name, CancellationToken ct) =>
        db.TeamGroups.AnyAsync(g => g.TeamId == teamId && g.Id != groupId && g.NameKey == name.ToLower(), ct);

    private static async Task<IReadOnlyList<TeamGroup>> GroupsOfAsync(
        NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var groups = await db.TeamGroups.AsNoTracking()
            .Where(g => g.TeamId == teamId)
            .OrderBy(g => g.NameKey)
            .ThenBy(g => g.Id)
            .Select(g => new
            {
                g.Id,
                g.Name,
                People = db.TeamTenantAccess
                    .Where(a => a.TeamId == teamId && g.People.Any(p => p.TenantId == a.TenantId))
                    .OrderBy(a => a.Tenant!.DisplayName)
                    .ThenBy(a => a.TenantId)
                    .Select(a => new { a.TenantId, a.Tenant!.DisplayName, a.AccessEndsAt })
                    .ToList(),
                Staff = g.Staff
                    .OrderBy(s => s.TeamMember!.SysCreatedAt)
                    .ThenBy(s => s.TeamMemberId)
                    .Select(s => new { s.TeamMember!.SubjectId, s.TeamMember.Subject!.Name, s.TeamMember.EndsAt })
                    .ToList(),
            })
            .ToListAsync(ct);

        return groups.Select(g =>
        {
            var people = g.People
                .Select(p => new TeamGroupPerson(p.TenantId, p.DisplayName, MembershipLiveness.HasEnded(p.AccessEndsAt, now)))
                .ToList();
            var staff = g.Staff
                .Select(s => new TeamGroupStaff(s.SubjectId, s.Name, MembershipLiveness.HasEnded(s.EndsAt, now)))
                .ToList();
            return new TeamGroup(g.Id, g.Name, people, staff, people.All(p => p.HasEnded) && staff.All(s => s.HasEnded));
        }).ToList();
    }

    /// <summary>
    /// How many pairs of a staff member and a site whose ends have not passed reach each other through the
    /// team now (<see cref="TeamReasons.GivesReason"/>) and share no group (<see cref="TeamReasons.SharesGroup"/>).
    /// </summary>
    private static async Task<int> AccessesOutsideGroupsAsync(NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var reaching = db.TeamSiteStaff.AsNoTracking()
            .Where(s => s.TeamTenantAccess!.TeamId == teamId
                && s.TeamMember!.Kind == TeamMemberKind.Staff
                && (s.TeamTenantAccess.AccessEndsAt == null || s.TeamTenantAccess.AccessEndsAt > now)
                && (s.TeamMember.EndsAt == null || s.TeamMember.EndsAt > now))
            .Where(TeamReasons.GivesReason);
        return await reaching.CountAsync(ct) - await reaching.CountAsync(TeamReasons.SharesGroup, ct);
    }
}
