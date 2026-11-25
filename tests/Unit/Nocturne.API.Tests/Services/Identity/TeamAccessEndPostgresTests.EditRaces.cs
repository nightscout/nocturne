using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner's edit of a team's access against the writes that change the same rows, each order forced at a
/// lock both take (<see cref="MembershipLock"/>): every pair finishes, and the result is the one the order gives.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    /// <remarks>
    /// The joiner joins after the edit read the team's staff and before it takes the team's manager lock, and
    /// their account deletion then holds their row and waits at that lock behind the edit. The widening gives the
    /// joiner their first reason, a membership naming them, so an edit that went on without their row locked would
    /// wait on the deletion's row while the deletion waits on the edit. It runs again instead.
    /// </remarks>
    [Fact]
    public async Task An_edit_runs_again_for_a_joiner_it_did_not_lock_and_their_account_deletion_finishes()
    {
        var site = await SiteAsync("race-edit-joiner-delete");
        var team = await TeamAsync("Clinic");
        var treatments = await TreatmentsRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [treatments]));
        var staff = await JoinStaffAsync(team);
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));

        await using var rowHolder = Context(site.Tenant, null);
        await using var rowHeld = await rowHolder.Database.BeginTransactionAsync(Ct);
        await rowHolder.Database.ExecuteSqlAsync($"SELECT 1 FROM subjects WHERE id = {staff} FOR UPDATE", Ct);
        var edit = EditAsync(site, team, new TeamAccessTerms([treatments, site.ViewerRole], false, null));
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, edit);

        (await Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct)).Refusal.Should().BeNull();
        (await ReasonAsync(site, joiner, access)).Should().BeNull("the ceiling shares nothing with what the access gives yet");
        await using var managerHolder = Context(site.Tenant, null);
        await using var managerHeld = await managerHolder.Database.BeginTransactionAsync(Ct);
        await managerHolder.LockTeamManagersAsync(team.Id, Ct);
        await rowHeld.RollbackAsync(Ct);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, edit);
        var delete = Deletion(joiner).DeleteAsync(joiner, false, Ct);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, edit, delete);
        await managerHeld.RollbackAsync(Ct);

        await delete.WaitAsync(Eventually.DefaultPatience);
        (await edit.WaitAsync(Eventually.DefaultPatience)).Should().Be(Edited);
        await IsGoneAsync(joiner);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([treatments, site.ViewerRole], null, false, null));
    }

    /// <remarks>
    /// Both wait at the team's manager lock. An edit ahead of the join is what the join then syncs from; a join
    /// ahead of the edit is a staff member the edit did not lock, so it runs again and syncs them.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_and_a_staff_join_at_once_both_finish_and_the_joiner_holds_what_the_edit_gives(bool editFirst)
    {
        var (site, team, access, _) = await StaffWithReasonAsync($"race-edit-join-{editFirst}");
        var joiner = await NewSubjectAsync("joiner");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        TeamAccessEdit? edited = null;

        Func<Task> edit = async () => edited = await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], true, null));
        Func<Task> join = () => Service(Guid.Empty, joiner).JoinStaffAsync(token, joiner, Ct);
        await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), editFirst ? edit : join, editFirst ? join : edit);

        edited.Should().Be(Edited);
        (await ReasonAsync(site, joiner, access)).Should().BeEquivalentTo(new Held([site.ViewerRole, site.CaretakerRole], null, true, null));
        (await OutOfLineAsync(joiner, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the site's owners lock. The edit gives the team a role the role edit makes run the site. An
    /// edit ahead of the role edit makes the role one a team holds, which refuses the role edit; a role edit
    /// ahead of the edit makes the role administrative, which refuses the edit. Either way no team holds a role
    /// that runs the site.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_and_a_role_edit_at_once_both_finish_and_no_team_holds_a_role_that_runs_the_site(bool editFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-edit-role-{editFirst}");
        var night = await NightRoleAsync(site);
        TeamAccessEdit? edited = null;
        TenantRoleUpdate? roleEdited = null;

        Func<Task> edit = async () => edited = await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, night], false, null));
        Func<Task> editRole = async () => roleEdited = await TeamReasonWrites.EditRoleAsync(
            Contexts(site.Tenant, site.Owner), new GrantRevocationService(NewSessions()), site.Tenant, night,
            new TenantRoleEdit("Night", "night", null, [Scope.GlucoseRead, Scope.MembersManage]), site.Owner, Now, Ct);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), editFirst ? edit : editRole, editFirst ? editRole : edit);

        if (editFirst)
        {
            (edited, roleEdited!.Outcome).Should().Be((Edited, RoleChangeOutcome.HeldByTeam));
            (await ReasonAsync(site, staff, access))!.RoleIds.Should().BeEquivalentTo([site.ViewerRole, night]);
        }
        else
        {
            (edited, roleEdited!.Outcome).Should().Be((new TeamAccessEdit(OwnerDecision.Decided, TeamAccessRefusal.RoleAdministrative), RoleChangeOutcome.Changed));
            (await ReasonAsync(site, staff, access))!.RoleIds.Should().Equal(site.ViewerRole);
        }

        (await Members(site.Tenant).GetRoleAsync(site.Tenant, night, Ct))!.Permissions.Should()
            .Equal(editFirst ? [Scope.GlucoseRead, Scope.TreatmentsRead] : [Scope.GlucoseRead, Scope.MembersManage]);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Both wait at the site's team access lock. An edit ahead of the stop gives what the stop then ends; a stop
    /// ahead of the edit leaves it no access to change.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_and_the_site_stopping_the_team_at_once_both_finish_and_leave_nothing(bool editFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-edit-stop-{editFirst}");
        TeamAccessEdit? edited = null;

        Func<Task> edit = async () => edited = await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], false, null));
        Func<Task> stop = () => Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct);
        await RaceAsync(site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), editFirst ? edit : stop, editFirst ? stop : edit);

        edited.Should().Be(editFirst ? Edited : new TeamAccessEdit(OwnerDecision.NotFound, null));
        (await AccessExistsAsync(access)).Should().BeFalse();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
    }

    /// <remarks>
    /// Both wait at the site's owners lock. An edit ahead of the step-down is an owner's; a step-down ahead of the
    /// edit leaves the caller no owner, and nothing changes.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_and_the_editors_step_down_at_once_both_finish_and_only_an_owner_edits(bool editFirst)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-edit-step-down-{editFirst}");
        var owner = await SecondOwnerAsync(site);
        var ownerRole = await OwnerRoleAsync(site);
        TeamAccessEdit? edited = null;
        OwnerGuardedChange? stepped = null;

        Func<Task> edit = async () => edited = await EditAsAsync(
            site, team, SessionOf(owner), new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], false, null));
        Func<Task> stepDown = async () => stepped = await ReplaceRolesAsync(site, owner, owner, ownerRole, site.CaretakerRole);
        await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), editFirst ? edit : stepDown, editFirst ? stepDown : edit);

        stepped.Should().Be(OwnerGuardedChange.Changed);
        edited.Should().Be(editFirst ? Edited : new TeamAccessEdit(OwnerDecision.NotOwner, null));
        (await ReasonAsync(site, staff, access))!.RoleIds.Should().BeEquivalentTo(
            editFirst ? [site.ViewerRole, site.CaretakerRole] : new[] { site.ViewerRole });
    }
}
