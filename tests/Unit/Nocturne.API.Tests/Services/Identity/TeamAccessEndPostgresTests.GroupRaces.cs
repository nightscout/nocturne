using FluentAssertions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A manager's group change against a staff member joining, a site's stop of one person, another change to the
/// same group and the switch of which people the staff reach, each order forced at a lock both take
/// (<see cref="MembershipLock"/>): every pair finishes and leaves no team reason out of line.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    /// <remarks>
    /// Both wait at the team's manager lock. A put ahead of the join is read by the join, which gives the
    /// newcomer nothing outside the group; a join ahead of the put is a staff member the put does not sync, as
    /// they are in no group.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Putting_a_site_in_a_group_and_a_staff_member_joining_at_once_both_finish_and_only_the_group_reaches_it(bool putFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-group-join-{putFirst}");
        var group = await GroupAsync(team);
        await PutStaffAsync(team, group, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsNoTeamReasonAsync(site, staff, access);
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        TeamGroupChange? put = null;
        TeamInviteAcceptance? joined = null;

        Func<Task> putSite = async () => put = await Managing(team).AddGroupPersonAsync(team.Id, team.Manager, group, site.Tenant, Ct);
        Func<Task> join = async () => joined = await Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), putFirst ? putSite : join, putFirst ? join : putSite);

        put.Should().Be(TeamGroupChange.Changed);
        joined!.Refusal.Should().BeNull();
        await HoldsTeamReasonAsync(site, staff, access);
        await HoldsNoTeamReasonAsync(site, joiner, access);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
        (await OutOfLineAsync(joiner, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the site's team access lock. Either way the stop holds against the shared group.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Putting_a_staff_member_in_a_group_and_a_stop_of_them_at_once_both_finish_and_they_have_no_reason(bool putFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-group-block-{putFirst}");
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsNoTeamReasonAsync(site, staff, access);
        TeamGroupChange? put = null;
        OwnerDecision? blocked = null;

        Func<Task> putStaff = async () => put = await Managing(team).AddGroupStaffAsync(team.Id, team.Manager, group, staff, Ct);
        Func<Task> block = async () => blocked = await BlockAsync(site, site.Owner, staff);
        await RaceAsync(site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), putFirst ? putStaff : block, putFirst ? block : putStaff);

        put.Should().Be(TeamGroupChange.Changed);
        blocked.Should().Be(OwnerDecision.Decided);
        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the team's manager lock. A switch ahead of the take-out narrows first and the take-out then
    /// removes the only shared group; a take-out ahead of the switch leaves the staff member in no group, which
    /// the switch then reads.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_switch_to_groups_and_taking_a_staff_member_out_of_one_at_once_both_finish_and_they_have_no_reason(bool switchFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-group-switch-{switchFirst}");
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        TeamGroupChange? switched = null;
        TeamGroupChange? taken = null;

        Func<Task> reach = async () => switched = await Managing(team).SetStaffReachAsync(team.Id, team.Manager, TeamStaffReach.TheirGroups, Ct);
        Func<Task> take = async () => taken = await Managing(team).RemoveGroupStaffAsync(team.Id, team.Manager, group, staff, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), switchFirst ? reach : take, switchFirst ? take : reach);

        switched.Should().Be(TeamGroupChange.Changed);
        taken.Should().Be(TeamGroupChange.Changed);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the team's manager lock. A put of the site behind a put of the staff member finds under the
    /// lock a staff member it did not read before it, so it runs again and syncs them; ahead of it, the put of
    /// the staff member reads the site in the group.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Putting_a_staff_member_and_a_site_in_one_group_at_once_both_finish_and_the_group_reaches_the_site(bool staffFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-group-group-{staffFirst}");
        var group = await GroupAsync(team);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsNoTeamReasonAsync(site, staff, access);
        TeamGroupChange? staffPut = null;
        TeamGroupChange? sitePut = null;

        Func<Task> putStaff = async () => staffPut = await Managing(team).AddGroupStaffAsync(team.Id, team.Manager, group, staff, Ct);
        Func<Task> putSite = async () => sitePut = await Managing(team).AddGroupPersonAsync(team.Id, team.Manager, group, site.Tenant, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), staffFirst ? putStaff : putSite, staffFirst ? putSite : putStaff);

        staffPut.Should().Be(TeamGroupChange.Changed);
        sitePut.Should().Be(TeamGroupChange.Changed);
        await HoldsTeamReasonAsync(site, staff, access);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the team's manager lock. A switch back to every person queued behind the join finds under the
    /// lock a staff member it did not read before it, so it runs again and syncs them: the put that follows
    /// syncs nobody once the team reaches every person. Ahead of the join, the join reads the switch.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_switch_to_every_person_and_a_new_staff_member_put_in_a_group_at_once_both_finish_and_they_reach_the_site(bool switchFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-group-reach-{switchFirst}");
        var group = await GroupAsync(team);
        await PutSiteAsync(team, group, site);
        await PutStaffAsync(team, group, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        await HoldsTeamReasonAsync(site, staff, access);
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        TeamGroupChange? switched = null;
        TeamInviteAcceptance? joined = null;
        TeamGroupChange? put = null;

        Func<Task> reach = async () => switched = await Managing(team).SetStaffReachAsync(team.Id, team.Manager, TeamStaffReach.EveryPerson, Ct);
        Func<Task> joinAndPut = async () =>
        {
            joined = await Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
            put = await Managing(team).AddGroupStaffAsync(team.Id, team.Manager, group, joiner, Ct);
        };
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), switchFirst ? reach : joinAndPut, switchFirst ? joinAndPut : reach);

        switched.Should().Be(TeamGroupChange.Changed);
        joined!.Refusal.Should().BeNull();
        put.Should().Be(TeamGroupChange.Changed);
        await HoldsTeamReasonAsync(site, staff, access);
        await HoldsTeamReasonAsync(site, joiner, access);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
        (await OutOfLineAsync(joiner, site)).Should().BeEmpty();
    }
}
