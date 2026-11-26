using FluentAssertions;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Trusting a team's whole staff, and stopping the trust, against a staff join and an owner's decision, each
/// order forced at a lock both take (<see cref="MembershipLock"/>): every pair finishes, and the result is the
/// one the order gives.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    /// <remarks>
    /// Both wait at the team's manager lock. A trust ahead of the join gives the joiner an approved row; a join
    /// ahead of the trust leaves a pending row, which the trust, run again for a staff member it did not lock,
    /// gives the reason. Either way no notice waits.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusting_the_team_and_a_staff_join_at_once_both_finish_and_the_joiner_reaches_the_site(bool trustFirst)
    {
        var (site, team, access, _) = await WaitingStaffAsync($"race-trust-join-{trustFirst}", new TeamStaffNarrowing(null, false, null));
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        OwnerDecision? decided = null;

        Func<Task> trust = async () => decided = await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted);
        Func<Task> join = () => Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), trustFirst ? trust : join, trustFirst ? join : trust);

        decided.Should().Be(OwnerDecision.Decided);
        (await StatusAsync(access, joiner)).Should().Be(trustFirst ? TeamSiteStaffStatus.Approved : TeamSiteStaffStatus.Pending);
        await HoldsTeamReasonAsync(site, joiner, access);
        Waiting(site.Owner).Should().BeNull();
    }

    /// <remarks>
    /// Both wait at the team's manager lock. Stopping the trust ahead of the join leaves the joiner waiting with
    /// no reason; a join ahead of it is approved as a trusted team's staff, and an owner approving keeps the
    /// staff already approved.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stopping_the_trust_and_a_staff_join_at_once_both_finish_and_the_order_decides_the_joiner(bool stopFirst)
    {
        var site = await SiteAsync($"race-untrust-join-{stopFirst}");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], staffRule: TeamStaffRule.TeamTrusted));
        await JoinStaffAsync(team);
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        OwnerDecision? decided = null;

        Func<Task> stop = async () => decided = await SetStaffRuleAsync(site, team, TeamStaffRule.OwnerApproves);
        Func<Task> join = () => Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), stopFirst ? stop : join, stopFirst ? join : stop);

        decided.Should().Be(OwnerDecision.Decided);
        if (stopFirst)
        {
            (await StatusAsync(access, joiner)).Should().Be(TeamSiteStaffStatus.Pending);
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, joiner, Ct)).Should().BeNull();
            CountOf(Waiting(site.Owner)).Should().Be(1);
        }
        else
        {
            (await StatusAsync(access, joiner)).Should().Be(TeamSiteStaffStatus.Approved);
            await HoldsTeamReasonAsync(site, joiner, access);
            Waiting(site.Owner).Should().BeNull();
        }
    }

    /// <remarks>
    /// Both wait at the site's owners lock. A decline ahead of the trust is held and does not apply; a trust ahead
    /// of the decline refuses it. Either way the staff member reaches the site.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusting_the_team_and_a_decline_at_once_both_finish_and_the_staff_member_reaches_the_site(bool declineFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-trust-decline-{declineFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? declined = null;
        OwnerDecision? trusted = null;

        Func<Task> decline = async () => declined = await DeclineStaffAsync(site, team, site.Owner, staff);
        Func<Task> trust = async () => trusted = await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), declineFirst ? decline : trust, declineFirst ? trust : decline);

        (declined, trusted).Should().Be((declineFirst ? OwnerDecision.Decided : OwnerDecision.TeamTrusted, OwnerDecision.Decided));
        (await StatusAsync(access, staff)).Should().Be(declineFirst ? TeamSiteStaffStatus.Declined : TeamSiteStaffStatus.Pending);
        (await DeclinesAsync(access, staff)).Should().Be(declineFirst ? 1 : 0);
        await HoldsTeamReasonAsync(site, staff, access);
    }

    /// <remarks>
    /// Both wait at the site's owners lock. An approval ahead of stopping the trust is refused, and the staff
    /// member then waits with no reason; stopping the trust ahead of the approval lets it through.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stopping_the_trust_and_an_approval_at_once_both_finish_and_only_an_approval_after_it_counts(bool approveFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-untrust-approve-{approveFirst}", new TeamStaffNarrowing(null, false, null));
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        await HoldsTeamReasonAsync(site, staff, access);
        OwnerDecision? approved = null;
        OwnerDecision? stopped = null;

        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        Func<Task> stop = async () => stopped = await SetStaffRuleAsync(site, team, TeamStaffRule.OwnerApproves);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), approveFirst ? approve : stop, approveFirst ? stop : approve);

        (approved, stopped).Should().Be((approveFirst ? OwnerDecision.TeamTrusted : OwnerDecision.Decided, OwnerDecision.Decided));
        if (approveFirst)
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Pending);
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        }
        else
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
            await HoldsTeamReasonAsync(site, staff, access);
        }
    }
}
