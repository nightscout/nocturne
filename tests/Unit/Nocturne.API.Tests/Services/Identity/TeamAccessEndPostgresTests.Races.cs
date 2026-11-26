using FluentAssertions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The writes that give team reasons against the account deletion of a staff member they reach, and the
/// role edit against a staff join, each order forced: each takes the subject's row before any other lock
/// (<see cref="MembershipLock"/>), so neither waits on the other while holding what the other needs.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private static readonly TenantRoleEdit NightWithReports =
        new("Night", "night", null, [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead]);

    private Task DeactivateAsync(Guid subject) =>
        NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"UPDATE subjects SET is_active = false WHERE id = '{subject}'");

    private async Task IsGoneAsync(Guid subject)
    {
        (await new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off).GetAsync(subject, Ct)).Should().BeNull();
        (await ReadAsync($"SELECT count(*)::text FROM tenant_members WHERE subject_id = '{subject}'")).Should().Equal("0");
        (await ReadAsync($"SELECT count(*)::text FROM team_members WHERE subject_id = '{subject}'")).Should().Equal("0");
    }

    private Task<TenantRoleUpdate> EditNightAsync(Site site, Guid night) =>
        TeamReasonWrites.EditRoleAsync(
            Contexts(site.Tenant, site.Owner), new GrantRevocationService(NewSessions()), site.Tenant, night, NightWithReports,
            site.Owner, Now, Ct);

    /// <remarks>
    /// The approval first holds the staff member's row and waits at their membership; the deletion first
    /// holds the row <c>FOR UPDATE</c> and waits at the team's manager lock. An inactive staff member's
    /// deletion takes the row first all the same.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task An_approval_and_a_staff_members_account_deletion_at_once_both_finish(bool approvalFirst, bool inactive)
    {
        var site = await SiteAsync($"race-approve-delete-{approvalFirst}-{inactive}");
        var team = await TeamAsync("Clinic");
        var staff = await JoinStaffAsync(team);
        var kept = await JoinStaffAsync(team);
        if (inactive)
            await DeactivateAsync(staff);
        var token = await AccessLinkAsync(team);
        TeamAccessApproval? approval = null;

        Func<Task> approve = async () => approval = await ApproveAsync(site, token, Gives(site, [site.ViewerRole]));
        Func<Task> delete = () => Deletion(staff).DeleteAsync(staff, false, Ct);
        if (approvalFirst)
            await RaceAsync(site, staff, approve, delete);
        else
            await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), delete, approve);

        approval!.Refusal.Should().BeNull();
        await IsGoneAsync(staff);
        await HoldsTeamReasonAsync(site, kept, approval.AccessId!.Value);
    }

    /// <remarks>
    /// The edit first holds the staff member's row and waits at their membership; the deletion first holds
    /// the row <c>FOR UPDATE</c> and waits at the site's owners lock.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_role_edit_and_a_staff_members_account_deletion_at_once_both_finish(bool editFirst)
    {
        var site = await SiteAsync($"race-role-delete-{editFirst}");
        var team = await TeamAsync("Clinic");
        var night = await NightRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [night]));
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        await HoldsTeamReasonAsync(site, staff, access);
        TenantRoleUpdate? edited = null;

        Func<Task> edit = async () => edited = await EditNightAsync(site, night);
        Func<Task> delete = () => Deletion(staff).DeleteAsync(staff, false, Ct);
        if (editFirst)
            await RaceAsync(site, staff, edit, delete);
        else
            await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), delete, edit);

        edited!.Outcome.Should().Be(RoleChangeOutcome.Changed);
        await IsGoneAsync(staff);
        (await Members(site.Tenant).GetRoleAsync(site.Tenant, night, Ct))!.Permissions.Should().Contain(Scope.ReportsRead);
    }

    /// <remarks>
    /// The staff edit first holds the staff member's row and waits at their membership; the deletion first
    /// holds the row <c>FOR UPDATE</c> and waits at the team's manager lock, and the edit then finds no
    /// staff member to edit.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_staff_edit_and_the_staff_members_account_deletion_at_once_both_finish(bool editFirst)
    {
        var (site, team, _, staff) = await StaffWithReasonAsync($"race-staff-edit-delete-{editFirst}");
        TeamStaffChange? changed = null;

        Func<Task> edit = async () => changed = await Service(Guid.Empty, team.Manager)
            .UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, true, Now.AddDays(5)), Ct);
        Func<Task> delete = () => Deletion(staff).DeleteAsync(staff, false, Ct);
        if (editFirst)
            await RaceAsync(site, staff, edit, delete);
        else
            await RaceAsync(site, holder => holder.LockTeamManagersAsync(team.Id, Ct), delete, edit);

        changed.Should().Be(editFirst ? TeamStaffChange.Changed : TeamStaffChange.NotFound);
        await IsGoneAsync(staff);
    }

    /// <remarks>
    /// Both wait at the site's team access lock, in the order given. A join ahead of the edit gives the
    /// reason from the role as it was; the edit then finds a staff member it did not lock, runs again,
    /// and recomputes it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_role_edit_and_a_staff_join_at_once_both_finish_and_the_reason_holds_the_edited_role(bool editFirst)
    {
        var site = await SiteAsync($"race-role-join-{editFirst}");
        var team = await TeamAsync("Clinic");
        var night = await NightRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [night]));
        var staff = await NewSubjectAsync("staff");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));

        Func<Task> edit = () => EditNightAsync(site, night);
        Func<Task> join = () => Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct);
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), editFirst ? edit : join, editFirst ? join : edit);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(
            new Held([], [Scope.GlucoseRead, Scope.ReportsRead], false, null), options => options.WithStrictOrdering());
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
    }
}
