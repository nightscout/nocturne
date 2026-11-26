using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A manager's groups and the switch of which people a team's staff reach, through <see cref="TeamAccessService"/>:
/// while the team reaches every person a group touches no membership; while it reaches only its groups a staff
/// member reaches a site only through a group they share, within the rule, the decline and the stop that hold
/// either way.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private async Task<Guid> GroupAsync(Team team, string name = "Ward")
    {
        var created = await Teams(team.Manager).CreateGroupAsync(team.Id, team.Manager, name, Ct);
        created.Change.Should().Be(TeamGroupChange.Changed);
        return created.GroupId!.Value;
    }

    private TeamAccessService Managing(Team team) => Service(Guid.Empty, team.Manager);

    private async Task PutSiteAsync(Team team, Guid group, Site site) =>
        (await Managing(team).AddGroupPersonAsync(team.Id, team.Manager, group, site.Tenant, Ct)).Should().Be(TeamGroupChange.Changed);

    private async Task TakeSiteAsync(Team team, Guid group, Site site) =>
        (await Managing(team).RemoveGroupPersonAsync(team.Id, team.Manager, group, site.Tenant, Ct)).Should().Be(TeamGroupChange.Changed);

    private async Task PutStaffAsync(Team team, Guid group, Guid staff) =>
        (await Managing(team).AddGroupStaffAsync(team.Id, team.Manager, group, staff, Ct)).Should().Be(TeamGroupChange.Changed);

    private async Task TakeStaffAsync(Team team, Guid group, Guid staff) =>
        (await Managing(team).RemoveGroupStaffAsync(team.Id, team.Manager, group, staff, Ct)).Should().Be(TeamGroupChange.Changed);

    private async Task ReachAsync(Team team, string staffReach) =>
        (await Managing(team).SetStaffReachAsync(team.Id, team.Manager, staffReach, Ct)).Should().Be(TeamGroupChange.Changed);

    private async Task<int> TeamRowsAsync(Team team) =>
        int.Parse((await ReadAsync($"SELECT count(*)::text FROM auth_audit_log WHERE team_id = '{team.Id}'")).Single());

    private async Task HoldsNoTeamReasonAsync(Site site, Guid staff, Guid access) =>
        (await KindsAsync(site, staff)).Should().NotContain($"{MembershipReasonKind.Team}:{access}");

    [Fact]
    public async Task While_the_team_reaches_every_person_no_group_change_touches_a_membership_or_writes_at_the_site()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("groups-off");
        var membership = await MembershipOfAsync(site, staff);
        var siteRows = await AuditCountAsync(site);
        var notices = _notices.All.Count;

        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        await TakeStaffAsync(team, group, staff);
        await TakeSiteAsync(team, group, site);
        await PutSiteAsync(team, group, site);
        (await Managing(team).DeleteGroupAsync(team.Id, team.Manager, group, Ct)).Should().Be(TeamGroupChange.Changed);

        (await MembershipOfAsync(site, staff)).Should().Equal(membership);
        (await AuditCountAsync(site)).Should().Be(siteRows);
        _notices.All.Should().HaveCount(notices);
    }

    [Fact]
    public async Task While_the_team_reaches_its_groups_only_a_shared_group_gives_access_and_losing_it_stops_what_they_made()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("groups-on");
        var made = await MadeByAsync(site, staff);

        await ReachAsync(team, TeamStaffReach.TheirGroups);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);

        var group = await GroupAsync(team);
        await PutStaffAsync(team, group, staff);
        await HoldsNoTeamReasonAsync(site, staff, access);
        await PutSiteAsync(team, group, site);
        await HoldsTeamReasonAsync(site, staff, access);

        await TakeStaffAsync(team, group, staff);
        await HoldsNoTeamReasonAsync(site, staff, access);
        await PutStaffAsync(team, group, staff);
        await HoldsTeamReasonAsync(site, staff, access);

        await TakeSiteAsync(team, group, site);
        await HoldsNoTeamReasonAsync(site, staff, access);
        await PutSiteAsync(team, group, site);
        await HoldsTeamReasonAsync(site, staff, access);

        var second = await GroupAsync(team, "Clinic");
        await PutSiteAsync(team, second, site);
        await PutStaffAsync(team, second, staff);
        (await Managing(team).DeleteGroupAsync(team.Id, team.Manager, group, Ct)).Should().Be(TeamGroupChange.Changed);
        await HoldsTeamReasonAsync(site, staff, access);
        (await Managing(team).DeleteGroupAsync(team.Id, team.Manager, second, Ct)).Should().Be(TeamGroupChange.Changed);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_site_reads_a_staff_member_as_outside_the_team_groups_only_while_that_team_narrows_and_they_share_none_of_its_groups()
    {
        var (site, first, _, second, _, staff) = await StaffOfTwoTeamsAsync("groups-outside", TeamStaffRule.TeamDecides);

        async Task<(string Reach, bool Outside)> SeenAsync(Team team)
        {
            var access = (await Teams().ListAccessOfTenantAsync(site.Tenant, Now, Ct)).Single(a => a.TeamId == team.Id);
            return (access.StaffReach, access.Staff.Single(s => s.SubjectId == staff).OutsideTeamGroups);
        }

        (await SeenAsync(first)).Should().Be((TeamStaffReach.EveryPerson, false));
        (await SeenAsync(second)).Should().Be((TeamStaffReach.EveryPerson, false));

        var others = await GroupAsync(second);
        await PutSiteAsync(second, others, site);
        await PutStaffAsync(second, others, staff);
        await ReachAsync(first, TeamStaffReach.TheirGroups);
        (await SeenAsync(first)).Should().Be((TeamStaffReach.TheirGroups, true));
        (await SeenAsync(second)).Should().Be((TeamStaffReach.EveryPerson, false));

        var group = await GroupAsync(first);
        await PutStaffAsync(first, group, staff);
        (await SeenAsync(first)).Should().Be((TeamStaffReach.TheirGroups, true));
        await PutSiteAsync(first, group, site);
        (await SeenAsync(first)).Should().Be((TeamStaffReach.TheirGroups, false));

        await ReachAsync(second, TeamStaffReach.TheirGroups);
        (await SeenAsync(second)).Should().Be((TeamStaffReach.TheirGroups, false));
        await ReachAsync(first, TeamStaffReach.EveryPerson);
        await TakeSiteAsync(first, group, site);
        (await SeenAsync(first)).Should().Be((TeamStaffReach.EveryPerson, false));
    }

    [Theory]
    [InlineData(TeamStaffRule.OwnerApproves)]
    [InlineData(TeamStaffRule.TeamDecides)]
    [InlineData(TeamStaffRule.TeamTrusted)]
    public async Task Switching_to_groups_and_back_gives_back_exactly_the_membership_and_changes_no_row(string rule)
    {
        var (site, team, firstAccess, _, secondAccess, staff) = await StaffOfTwoTeamsAsync($"reach-{rule.Replace('_', '-')}", rule);
        var membership = await MembershipOfAsync(site, staff);
        var rows = await RowsAndDeclinesAsync(firstAccess);

        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsNoTeamReasonAsync(site, staff, firstAccess);
        await HoldsTeamReasonAsync(site, staff, secondAccess);
        (await RowsAndDeclinesAsync(firstAccess)).Should().Equal(rows);

        await ReachAsync(team, TeamStaffReach.EveryPerson);
        (await MembershipOfAsync(site, staff)).Should().Equal(membership);
        (await RowsAndDeclinesAsync(firstAccess)).Should().Equal(rows);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_shared_group_lifts_no_decline_no_pending_row_and_no_stop()
    {
        var staffed = await EachStatusAsync("groups-hold");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        foreach (var staff in new[] { staffed.Approved, staffed.Pending, staffed.Declined })
            await PutStaffAsync(team, group, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);

        await HoldsTeamReasonAsync(site, staffed.Approved, access);
        await HoldsNoTeamReasonAsync(site, staffed.Pending, access);
        await HoldsNoTeamReasonAsync(site, staffed.Declined, access);

        (await BlockAsync(site, site.Owner, staffed.Approved)).Should().Be(OwnerDecision.Decided);
        await TakeStaffAsync(team, group, staffed.Approved);
        await PutStaffAsync(team, group, staffed.Approved);
        await HoldsNoTeamReasonAsync(site, staffed.Approved, access);

        (await UnblockAsync(site, site.Owner, staffed.Approved)).Should().Be(OwnerDecision.Decided);
        await HoldsTeamReasonAsync(site, staffed.Approved, access);
        foreach (var staff in new[] { staffed.Approved, staffed.Pending, staffed.Declined })
            (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task Trusting_the_team_gives_access_to_staff_who_share_a_group_and_not_to_the_rest()
    {
        var staffed = await EachStatusAsync("groups-trusted");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        var outside = await JoinStaffAsync(team);
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        foreach (var staff in new[] { staffed.Approved, staffed.Pending, staffed.Declined })
            await PutStaffAsync(team, group, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);

        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);

        foreach (var staff in new[] { staffed.Approved, staffed.Pending, staffed.Declined })
            await HoldsTeamReasonAsync(site, staff, access);
        await HoldsNoTeamReasonAsync(site, outside, access);
    }

    [Fact]
    public async Task The_owners_are_told_only_of_staff_waiting_who_would_reach_the_site_once_approved()
    {
        var (site, team, _, staff) = await WaitingStaffAsync("groups-waiting", new TeamStaffNarrowing(null, false, null));
        CountOf(Waiting(site.Owner)).Should().Be(1);

        await ReachAsync(team, TeamStaffReach.TheirGroups);
        Waiting(site.Owner).Should().BeNull();

        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        Waiting(site.Owner).Should().BeNull();
        await PutStaffAsync(team, group, staff);
        CountOf(Waiting(site.Owner)).Should().Be(1);

        await TakeStaffAsync(team, group, staff);
        Waiting(site.Owner).Should().BeNull();
        await ReachAsync(team, TeamStaffReach.EveryPerson);
        CountOf(Waiting(site.Owner)).Should().Be(1);
    }

    [Fact]
    public async Task Only_a_manager_changes_groups_and_a_missing_group_site_or_staff_member_is_not_found()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("groups-refused");
        var group = await GroupAsync(team);
        var stranger = await NewSubjectAsync("stranger");
        var other = await SiteAsync("groups-unfollowed");
        var otherTeam = await TeamAsync("Camp");
        var otherGroup = await GroupAsync(otherTeam);
        var rows = await TeamRowsAsync(team);

        foreach (var caller in new[] { staff, stranger })
        {
            var service = Service(Guid.Empty, caller);
            (await Teams(caller).CreateGroupAsync(team.Id, caller, "Mine", Ct)).Change.Should().Be(TeamGroupChange.NotFound);
            (await Teams(caller).RenameGroupAsync(team.Id, caller, group, "Mine", Ct)).Should().Be(TeamGroupChange.NotFound);
            (await service.AddGroupPersonAsync(team.Id, caller, group, site.Tenant, Ct)).Should().Be(TeamGroupChange.NotFound);
            (await service.AddGroupStaffAsync(team.Id, caller, group, staff, Ct)).Should().Be(TeamGroupChange.NotFound);
            (await service.DeleteGroupAsync(team.Id, caller, group, Ct)).Should().Be(TeamGroupChange.NotFound);
            (await service.SetStaffReachAsync(team.Id, caller, TeamStaffReach.TheirGroups, Ct)).Should().Be(TeamGroupChange.NotFound);
        }

        var manager = Managing(team);
        (await manager.AddGroupPersonAsync(team.Id, team.Manager, otherGroup, site.Tenant, Ct)).Should().Be(TeamGroupChange.NotFound);
        (await manager.AddGroupPersonAsync(team.Id, team.Manager, group, other.Tenant, Ct)).Should().Be(TeamGroupChange.NotFound);
        (await manager.AddGroupStaffAsync(team.Id, team.Manager, group, stranger, Ct)).Should().Be(TeamGroupChange.NotFound);
        (await manager.AddGroupStaffAsync(team.Id, team.Manager, group, team.Manager, Ct)).Should().Be(TeamGroupChange.NotFound);
        (await manager.RemoveGroupStaffAsync(team.Id, team.Manager, group, stranger, Ct)).Should().Be(TeamGroupChange.NotFound);
        (await manager.DeleteGroupAsync(team.Id, team.Manager, otherGroup, Ct)).Should().Be(TeamGroupChange.NotFound);
        await SetStaffEndAsync(staff, Passed);
        (await manager.AddGroupStaffAsync(team.Id, team.Manager, group, staff, Ct)).Should().Be(TeamGroupChange.NotFound);
        await FluentActions.Awaiting(() => manager.SetStaffReachAsync(team.Id, team.Manager, "nobody", Ct))
            .Should().ThrowAsync<ArgumentException>();

        (await TeamRowsAsync(team)).Should().Be(rows);
    }

    [Fact]
    public async Task A_group_name_is_taken_in_its_team_whatever_its_case_and_a_change_to_nothing_writes_nothing()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("groups-names");
        var group = await GroupAsync(team);
        var second = await GroupAsync(team, "Clinic");
        var teams = Teams(team.Manager);

        (await teams.CreateGroupAsync(team.Id, team.Manager, "WARD", Ct)).Change.Should().Be(TeamGroupChange.NameTaken);
        (await teams.RenameGroupAsync(team.Id, team.Manager, second, "ward", Ct)).Should().Be(TeamGroupChange.NameTaken);
        (await teams.RenameGroupAsync(team.Id, team.Manager, group, "WARD", Ct)).Should().Be(TeamGroupChange.Changed);
        (await GroupAsync(await TeamAsync("Camp"))).Should().NotBeEmpty();

        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        var rows = await TeamRowsAsync(team);
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        await TakeSiteAsync(team, second, site);
        await TakeStaffAsync(team, second, staff);
        await ReachAsync(team, TeamStaffReach.EveryPerson);
        (await teams.RenameGroupAsync(team.Id, team.Manager, group, "WARD", Ct)).Should().Be(TeamGroupChange.Changed);
        (await TeamRowsAsync(team)).Should().Be(rows);
    }

    [Fact]
    public async Task A_staff_members_removal_and_account_deletion_take_their_group_places_and_a_stopped_site_keeps_its_place()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("groups-life");
        var removed = await JoinStaffAsync(team);
        var deleted = await JoinStaffAsync(team);
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        foreach (var each in new[] { staff, removed, deleted })
            await PutStaffAsync(team, group, each);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsTeamReasonAsync(site, staff, access);

        (await Managing(team).RemoveStaffAsync(team.Id, team.Manager, removed, Ct)).Should().BeTrue();
        await Deletion(deleted).DeleteAsync(deleted, false, Ct);
        (await ReadAsync($"SELECT count(*)::text FROM team_group_staff WHERE team_group_id = '{group}'")).Should().Equal("1");

        (await Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct)).Should().BeTrue();
        var stopped = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!;
        stopped.Groups.Single().People.Should().BeEmpty();

        var again = await FollowAsync(team, site);
        await HoldsTeamReasonAsync(site, staff, again);
        var followed = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!;
        followed.Groups.Single().People.Select(p => p.TenantId).Should().Equal(site.Tenant);
        followed.Groups.Single().Staff.Select(s => s.SubjectId).Should().Equal(staff);
    }

    [Fact]
    public async Task A_manager_reads_the_groups_the_reach_and_how_many_would_stop_and_a_staff_member_reads_none_of_it()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("groups-read");
        var outside = await JoinStaffAsync(team);
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        var empty = await GroupAsync(team, "Empty");

        var detail = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!;
        detail.StaffReach.Should().Be(TeamStaffReach.EveryPerson);
        detail.AccessesOutsideGroups.Should().Be(1);
        detail.Groups.Select(g => g.Name).Should().Equal("Empty", "Ward");
        detail.Groups.Single(g => g.Id == empty).HasNobodyCurrent.Should().BeTrue();
        detail.Groups.Single(g => g.Id == group).Should().BeEquivalentTo(new
        {
            Id = group,
            Name = "Ward",
            People = new[] { new TeamGroupPerson(site.Tenant, "team-access-end", false) },
            HasNobodyCurrent = false,
        });
        detail.Staff.Single(s => s.SubjectId == staff).InNoGroup.Should().BeFalse();
        detail.Staff.Single(s => s.SubjectId == outside).InNoGroup.Should().BeTrue();

        await ReachAsync(team, TeamStaffReach.TheirGroups);
        detail = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!;
        detail.StaffReach.Should().Be(TeamStaffReach.TheirGroups);
        detail.AccessesOutsideGroups.Should().Be(0);
        detail.Staff.Single(s => s.SubjectId == staff).SitesApproved.Should().Be(1);
        detail.Staff.Single(s => s.SubjectId == outside).SitesApproved.Should().Be(0);

        var asStaff = (await Teams(staff).GetAsync(team.Id, staff, Now, Ct))!;
        asStaff.StaffReach.Should().BeNull();
        asStaff.Groups.Should().BeEmpty();
        asStaff.AccessesOutsideGroups.Should().Be(0);
    }

    [Fact]
    public async Task The_site_reads_each_group_change_that_moved_its_access_as_the_teams_naming_no_group_and_no_manager()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("groups-history");
        var group = await GroupAsync(team, "Secret ward");
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await TakeStaffAsync(team, group, staff);
        await PutStaffAsync(team, group, staff);
        await TakeSiteAsync(team, group, site);
        await PutSiteAsync(team, group, site);
        (await Managing(team).DeleteGroupAsync(team.Id, team.Manager, group, Ct)).Should().Be(TeamGroupChange.Changed);

        var page = await new MemberHistoryReader(Contexts(site.Tenant)).GetAsync(site.Tenant, null, 100, 0, Now, Ct);
        var groupKinds = new[]
        {
            MemberHistoryKind.TeamStaffReachChanged, MemberHistoryKind.TeamGroupStaffRemoved, MemberHistoryKind.TeamGroupStaffAdded,
            MemberHistoryKind.TeamGroupPersonRemoved, MemberHistoryKind.TeamGroupPersonAdded, MemberHistoryKind.TeamGroupDeleted,
        };
        var entries = page.Data.Where(e => groupKinds.Contains(e.Kind)).ToList();
        entries.Select(e => e.Kind).Reverse().Should().Equal(
            MemberHistoryKind.TeamStaffReachChanged,
            MemberHistoryKind.TeamGroupStaffRemoved,
            MemberHistoryKind.TeamGroupStaffAdded,
            MemberHistoryKind.TeamGroupPersonRemoved,
            MemberHistoryKind.TeamGroupPersonAdded,
            MemberHistoryKind.TeamGroupDeleted);
        entries.Should().OnlyContain(e => e.Actor.Kind == MemberHistoryActorKind.System && e.Team!.Name == team.Name);
        entries.Single(e => e.Kind == MemberHistoryKind.TeamStaffReachChanged).StaffReach.Should().Be(TeamStaffReach.TheirGroups);
        entries.Where(e => e.Kind is MemberHistoryKind.TeamGroupStaffAdded or MemberHistoryKind.TeamGroupStaffRemoved)
            .Should().OnlyContain(e => e.Member!.SubjectId == staff);
        (await ReadAsync($"SELECT details::text FROM auth_audit_log WHERE tenant_id = '{site.Tenant}'"))
            .Should().NotContain(d => d.Contains("Secret ward") || d.Contains(group.ToString()) || d.Contains(team.Manager.ToString()));
    }
}
