using FluentAssertions;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner stopping one person through every team against that person joining another team that follows the
/// site, an approval and a switch to trusting the whole team, each order forced at a lock both take
/// (<see cref="MembershipLock"/>): every pair finishes, and the person ends with no team reason.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    /// <remarks>
    /// Both are ordered by the site's team access lock, which each takes first and where the order is forced
    /// here, and again by the membership lock their syncs take before they read. The subject's row, which both
    /// lock too, is <c>KEY SHARE</c> and keeps neither from the other. A stop ahead of the join is read by the
    /// join, which gives no reason; a join ahead of the stop gives one, which the stop removes.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_and_the_person_joining_another_team_at_once_both_finish_and_the_person_has_no_reason(bool blockFirst)
    {
        var (site, _, _, staff) = await StaffWithReasonAsync($"race-block-join-{blockFirst}");
        var other = await TeamAsync("Camp");
        var otherAccess = await FollowAsync(other, site);
        var token = await StaffInviteAsync(other, new TeamStaffNarrowing(null, false, null));
        OwnerDecision? decided = null;
        TeamInviteAcceptance? joined = null;

        Func<Task> block = async () => decided = await BlockAsync(site, site.Owner, staff);
        Func<Task> join = async () => joined = await Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct);
        await RaceAsync(site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), blockFirst ? block : join, blockFirst ? join : block);

        decided.Should().Be(OwnerDecision.Decided);
        joined!.Refusal.Should().BeNull();
        (await StatusAsync(otherAccess, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the site's owners lock. Either way the approval is recorded on the row and gives nothing while
    /// the stop stands.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_and_an_approval_at_once_both_finish_and_the_approved_person_has_no_reason(bool blockFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-block-approve-{blockFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? blocked = null;
        OwnerDecision? approved = null;

        Func<Task> block = async () => blocked = await BlockAsync(site, site.Owner, staff);
        Func<Task> approve = async () => approved = await ApproveStaffAsync(site, team, site.Owner, staff);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), blockFirst ? block : approve, blockFirst ? approve : block);

        (blocked, approved).Should().Be((OwnerDecision.Decided, OwnerDecision.Decided));
        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
        Waiting(site.Owner).Should().BeNull();
    }

    /// <remarks>
    /// Both wait at the site's owners lock. Trust does not lift the stop, so either way the person has no reason
    /// and the rule is trusted.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_and_trusting_the_whole_team_at_once_both_finish_and_the_person_has_no_reason(bool blockFirst)
    {
        var (site, team, access, staff) = await WaitingStaffAsync($"race-block-trust-{blockFirst}", new TeamStaffNarrowing(null, false, null));
        OwnerDecision? blocked = null;
        OwnerDecision? trusted = null;

        Func<Task> block = async () => blocked = await BlockAsync(site, site.Owner, staff);
        Func<Task> trust = async () => trusted = await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), blockFirst ? block : trust, blockFirst ? trust : block);

        (blocked, trusted).Should().Be((OwnerDecision.Decided, OwnerDecision.Decided));
        (await StoredRuleAsync(access)).Should().Be(TeamStaffRule.TeamTrusted);
        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Pending);
        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }
}
