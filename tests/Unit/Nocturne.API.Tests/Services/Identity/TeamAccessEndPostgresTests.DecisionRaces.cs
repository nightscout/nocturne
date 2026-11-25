using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner's decisions about a team's staff against the writes that change the same rows, each order
/// forced at a lock both take (<see cref="MembershipLock"/>): every pair finishes, and the result is the
/// one the order gives.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    /// <summary>A site whose owners decide the team's staff, and a staff member of the team waiting there.</summary>
    private async Task<(Site Site, Team Team, Guid Access, Guid Staff)> WaitingStaffAsync(string slug, TeamStaffNarrowing narrowing)
    {
        var site = await SiteAsync(slug);
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await NewSubjectAsync("staff");
        (await JoinAsync(team, staff, narrowing)).Refusal.Should().BeNull();
        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Pending);
        return (site, team, access, staff);
    }

    /// <remarks>
    /// Both wait at the team's manager lock. A switch ahead of the join turns it on before the join reads
    /// it; a join ahead of the switch is a staff member the switch did not lock, so it runs again and
    /// approves them.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Turning_the_switch_on_and_a_staff_join_at_once_both_finish_and_the_joiner_is_approved(bool switchFirst)
    {
        var (site, team, access, _) = await WaitingStaffAsync($"race-switch-join-{switchFirst}", new TeamStaffNarrowing(null, false, null));
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        OwnerDecision? decided = null;

        Func<Task> switchOn = async () => decided = await SetStaffRuleAsync(site, team, true);
        Func<Task> join = () => Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await RaceAsync(
            site, holder => holder.LockTeamManagersAsync(team.Id, Ct), switchFirst ? switchOn : join, switchFirst ? join : switchOn);

        decided.Should().Be(OwnerDecision.Decided);
        (await StatusAsync(access, joiner)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(site, joiner, access);
    }

    /// <remarks>
    /// The joiner joins after the switch read the team's staff and before it takes the team's manager lock,
    /// and their account deletion then holds their row and waits at that lock behind the switch. The switch
    /// has not locked the joiner's row, so it runs again rather than approve them; approving them would make
    /// their membership wait on the deletion's row while the deletion waits on the switch, a deadlock.
    /// </remarks>
    [Fact]
    public async Task Turning_the_switch_on_runs_again_for_a_joiner_it_did_not_lock_and_their_account_deletion_finishes()
    {
        var (site, team, access, staff) = await WaitingStaffAsync("race-switch-joiner-delete", new TeamStaffNarrowing(null, false, null));
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));

        await using var rowHolder = Context(site.Tenant, null);
        await using var rowHeld = await rowHolder.Database.BeginTransactionAsync(Ct);
        await rowHolder.Database.ExecuteSqlAsync($"SELECT 1 FROM subjects WHERE id = {staff} FOR UPDATE", Ct);
        var switchOn = SetStaffRuleAsync(site, team, true);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, switchOn);

        await Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await using var managerHolder = Context(site.Tenant, null);
        await using var managerHeld = await managerHolder.Database.BeginTransactionAsync(Ct);
        await managerHolder.LockTeamManagersAsync(team.Id, Ct);
        await rowHeld.RollbackAsync(Ct);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, switchOn);
        var delete = Deletion(joiner).DeleteAsync(joiner, false, Ct);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, switchOn, delete);
        await managerHeld.RollbackAsync(Ct);

        await delete.WaitAsync(Eventually.DefaultPatience);
        (await switchOn.WaitAsync(Eventually.DefaultPatience)).Should().Be(OwnerDecision.Decided);
        await IsGoneAsync(joiner);
        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(site, staff, access);
    }

    /// <remarks>Both wait at the site's owners lock. Either way the decline holds.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_decline_and_turning_the_switch_on_at_once_both_finish_and_the_staff_member_stays_declined(bool declineFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-decline-switch-{declineFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? declined = null;
        OwnerDecision? switched = null;

        Func<Task> decline = async () => declined = await DeclineStaffAsync(site, team, site.Owner, staff);
        Func<Task> switchOn = async () => switched = await SetStaffRuleAsync(site, team, true);
        await RaceAsync(
            site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), declineFirst ? decline : switchOn, declineFirst ? switchOn : decline);

        (declined, switched).Should().Be((OwnerDecision.Decided, OwnerDecision.Decided));
        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Declined);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
    }

    /// <remarks>
    /// Both wait at the site's team access lock. An approval ahead of the removal gives the reason the
    /// removal then ends; a removal ahead of the approval leaves it no staff member to approve.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_the_staff_members_removal_at_once_both_finish_and_leave_them_nothing(bool approveFirst)
    {
        var (site, team, _, staff) = await WaitingStaffAsync($"race-approve-remove-staff-{approveFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? approved = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        Func<Task> remove = () => Service(Guid.Empty, team.Manager).RemoveStaffAsync(team.Id, team.Manager, staff, Ct);
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), approveFirst ? approve : remove, approveFirst ? remove : approve);

        approved.Should().Be(approveFirst ? OwnerDecision.Decided : OwnerDecision.NotFound);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await ReadAsync($"SELECT count(*)::text FROM team_members WHERE subject_id = '{staff}'")).Should().Equal("0");
    }

    /// <remarks>
    /// Both wait at the site's team access lock. An approval ahead of the widening is sent back to waiting
    /// by it; a widening ahead of the approval leaves the row waiting, and the approval gives what the
    /// widened staff member now holds.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_a_widening_at_once_both_finish_and_the_later_one_decides(bool approveFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-approve-widen-{approveFirst}", new TeamStaffNarrowing(RoleSeeds.Viewer, true, null));
        OwnerDecision? approved = null;
        TeamStaffChange? widened = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        Func<Task> widen = async () => widened = await Service(Guid.Empty, team.Manager)
            .UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, null), Ct);
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), approveFirst ? approve : widen, approveFirst ? widen : approve);

        (approved, widened).Should().Be((OwnerDecision.Decided, TeamStaffChange.Changed));
        if (approveFirst)
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Pending);
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        }
        else
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
            (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
        }
    }

    /// <remarks>
    /// Both wait at the site's team access lock. An approval ahead of the stop gives the reason the stop
    /// then ends; a stop ahead of the approval leaves it no team to decide about.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_the_site_stopping_the_team_at_once_both_finish_and_leave_nothing(bool approveFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-approve-stop-{approveFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? approved = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        Func<Task> stop = () => Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct);
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), approveFirst ? approve : stop, approveFirst ? stop : approve);

        approved.Should().Be(approveFirst ? OwnerDecision.Decided : OwnerDecision.NotFound);
        (await AccessExistsAsync(access)).Should().BeFalse();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
    }

    /// <remarks>
    /// Both wait at the site's owners lock. An approval ahead of the step-down is an owner's; a step-down
    /// ahead of the approval leaves the caller no owner, and nothing is approved.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_the_approvers_step_down_at_once_both_finish_and_only_an_owner_approves(bool approveFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-approve-step-down-{approveFirst}", new TeamStaffNarrowing(null, false, null));
        var owner = await SecondOwnerAsync(site);
        var ownerRole = await OwnerRoleAsync(site);
        OwnerDecision? approved = null;
        OwnerGuardedChange? stepped = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, owner, staff);
        Func<Task> stepDown = async () => stepped = await ReplaceRolesAsync(site, owner, owner, ownerRole, site.CaretakerRole);
        await RaceAsync(
            site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), approveFirst ? approve : stepDown, approveFirst ? stepDown : approve);

        stepped.Should().Be(OwnerGuardedChange.Changed);
        approved.Should().Be(approveFirst ? OwnerDecision.Decided : OwnerDecision.NotOwner);
        (await StatusAsync(access, staff)).Should().Be(approveFirst ? TeamSiteStaffStatus.Approved : TeamSiteStaffStatus.Pending);
    }

    /// <remarks>
    /// The approval first holds the staff member's row and waits at their membership; the deletion first
    /// holds the row <c>FOR UPDATE</c> and waits at the team's manager lock, and the approval then finds no
    /// staff member. Neither ends in a deadlock.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_the_staff_members_account_deletion_at_once_both_finish(bool approveFirst)
    {
        var (site, team, _, staff) = await WaitingStaffAsync($"race-approve-delete-{approveFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? approved = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        Func<Task> delete = () => Deletion(staff).DeleteAsync(staff, false, Ct);
        if (approveFirst)
            await RaceAsync(site, staff, approve, delete);
        else
            await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), delete, approve);

        approved.Should().Be(approveFirst ? OwnerDecision.Decided : OwnerDecision.NotFound);
        await IsGoneAsync(staff);
    }
}
