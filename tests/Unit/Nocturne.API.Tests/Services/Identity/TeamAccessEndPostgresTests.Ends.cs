using System.Globalization;
using FluentAssertions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// What the sync does with an end that has passed, on the staff member or on the access, and with a
/// ceiling that shares nothing with what the access gives.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private static readonly DateTime Passed = Now.AddHours(-1);

    public enum Ended
    {
        Staff,
        Access,
    }

    private static string Sql(DateTime instant) =>
        instant.ToString("yyyy-MM-dd HH:mm:ss.ffffff'+00'", CultureInfo.InvariantCulture);

    private Task SetStaffEndAsync(Guid staff, DateTime end) =>
        NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_members SET ends_at = '{Sql(end)}' WHERE subject_id = '{staff}'");

    private Task SetAccessEndAsync(Site site, Guid access, DateTime end) =>
        NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE team_tenant_access SET access_ends_at = '{Sql(end)}' WHERE id = '{access}';
            """);

    /// <summary>Moves the end of the reason <paramref name="access"/> gives, and the row's, as a build that synced at that end left them.</summary>
    private Task SetReasonEndAsync(Site site, Guid staff, Guid access, DateTime end) =>
        NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE tenant_member_reasons SET access_ends_at = '{Sql(end)}'
             WHERE source_id = '{access}'
               AND tenant_member_id IN (SELECT id FROM tenant_members WHERE tenant_id = '{site.Tenant}' AND subject_id = '{staff}');
            UPDATE tenant_members SET access_ends_at = '{Sql(end)}' WHERE tenant_id = '{site.Tenant}' AND subject_id = '{staff}';
            """);

    private Task EndAsync(Ended ended, Site site, Guid staff, Guid access) =>
        ended == Ended.Staff ? SetStaffEndAsync(staff, Passed) : SetAccessEndAsync(site, access, Passed);

    /// <summary>The subject holds no reason from <paramref name="access"/>, no membership of the site, and no joined row there.</summary>
    private async Task HoldsNothingAsync(Site site, Guid staff, Guid access)
    {
        (await ReasonAsync(site, staff, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await RowsAsync(site.Tenant, staff)).Should().NotContain(r => r.EventType == AuthAuditEventType.MemberJoined);
    }

    [Fact]
    public async Task A_staff_member_whose_end_passed_and_is_extended_holds_the_reason_again()
    {
        var site = await SiteAsync("extend");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site);
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, Now.AddDays(1)));
        await SetStaffEndAsync(staff, Passed);
        await SetReasonEndAsync(site, staff, access, Passed);
        var extended = Now.AddDays(10);

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, extended), Ct))
            .Should().Be(TeamStaffChange.Changed);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, extended));
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.AccessEndsAt.Should().Be(extended);
        (await Members(site.Tenant).IsMemberAsync(site.Tenant, staff, Now, Ct)).Should().BeTrue();
        await RowIsTheSumAsync(site, staff);
        (await RowsAsync(site.Tenant, staff)).Where(r => r.EventType == AuthAuditEventType.MemberJoined)
            .Should().HaveCount(2, "the history says the member has access through the team again")
            .And.OnlyContain(r => r.Team == team.Id);
    }

    [Fact]
    public async Task The_startup_check_gives_back_a_lapsed_reason_whose_source_now_ends_later()
    {
        var site = await SiteAsync("extend-check");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site);
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, Now.AddDays(1)));
        var extended = Now.AddDays(10);
        await SetStaffEndAsync(staff, extended);
        await SetReasonEndAsync(site, staff, access, Passed);

        await ReconcileAsync();

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, extended));
        (await Members(site.Tenant).IsMemberAsync(site.Tenant, staff, Now, Ct)).Should().BeTrue();
        await RowIsTheSumAsync(site, staff);
    }

    [Fact]
    public async Task A_lapsed_reason_whose_source_has_ended_too_is_left_as_it_is()
    {
        var site = await SiteAsync("both-ended");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site);
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, Now.AddDays(1)));
        await SetStaffEndAsync(staff, Now.AddMinutes(-30));
        await SetReasonEndAsync(site, staff, access, Passed);
        var audited = await AuditCountAsync(site);

        await ReconcileAsync();

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, Passed));
        (await AuditCountAsync(site)).Should().Be(audited);
    }

    [Fact]
    public async Task A_join_gives_no_reason_where_the_access_has_ended()
    {
        var site = await SiteAsync("join-access-ended");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], end: Now.AddDays(1)));
        var staff = await NewSubjectAsync("staff");
        await SetAccessEndAsync(site, access, Passed);

        (await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, null))).Refusal.Should().BeNull();

        await HoldsNothingAsync(site, staff, access);
    }

    [Fact]
    public async Task A_join_whose_own_end_has_passed_is_refused_and_gives_nothing()
    {
        var site = await SiteAsync("join-staff-ended");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site);
        var staff = await NewSubjectAsync("staff");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, Now.AddHours(1)));
        await NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_staff_invites SET staff_ends_at = '{Sql(Passed)}' WHERE token_hash = '{token}'");

        (await Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct)).Refusal.Should().Be(TeamInviteRefusal.StaffEndPassed);

        await HoldsNothingAsync(site, staff, access);
    }

    [Fact]
    public async Task An_approval_gives_no_reason_to_a_staff_member_whose_end_has_passed()
    {
        var site = await SiteAsync("approve-staff-ended");
        var team = await TeamAsync("Clinic");
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, Now.AddDays(1)));
        await SetStaffEndAsync(staff, Passed);

        var access = await FollowAsync(team, site);

        await HoldsNothingAsync(site, staff, access);
    }

    [Fact]
    public async Task An_approval_whose_end_has_passed_is_refused_and_gives_nothing()
    {
        var site = await SiteAsync("approve-access-ended");
        var team = await TeamAsync("Clinic");
        var staff = await JoinStaffAsync(team);

        var approval = await ApproveAsync(site, await AccessLinkAsync(team), Gives(site, [site.ViewerRole], end: Passed));

        approval.Refusal.Should().NotBeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await RowsAsync(site.Tenant, staff)).Should().NotContain(r => r.EventType == AuthAuditEventType.MemberJoined);
    }

    [Theory]
    [InlineData(Ended.Staff)]
    [InlineData(Ended.Access)]
    public async Task The_startup_check_gives_no_reason_where_an_end_has_passed(Ended ended)
    {
        var site = await SiteAsync($"backfill-ended-{ended}");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false, end: Now.AddDays(1)));
        var staff = await JoinStaffAsync(team);
        await ApproveSiteRowsAsync(site);
        await EndAsync(ended, site, staff, access);

        await ReconcileAsync();

        await HoldsNothingAsync(site, staff, access);
    }

    [Fact]
    public async Task A_staff_member_whose_ceiling_shares_nothing_with_the_access_holds_no_reason()
    {
        var site = await SiteAsync("empty-meet");
        var team = await TeamAsync("Clinic");
        var treatments = await TreatmentsRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [treatments]));
        var staff = await NewSubjectAsync("staff");

        (await JoinAsync(team, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null))).Refusal.Should().BeNull();

        await HoldsNothingAsync(site, staff, access);
    }

    [Fact]
    public async Task A_ceiling_narrowed_to_share_nothing_with_the_access_ends_the_reason_and_stops_what_they_made()
    {
        var site = await SiteAsync("empty-meet-narrowed");
        var team = await TeamAsync("Clinic");
        var treatments = await TreatmentsRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [treatments]));
        var staff = await JoinStaffAsync(team);
        await HoldsTeamReasonAsync(site, staff, access);
        var made = await MadeByAsync(site, staff);

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);

        (await ReasonAsync(site, staff, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
    }

    [Fact]
    public async Task A_write_that_finds_a_new_staff_member_on_every_attempt_gives_up_naming_itself()
    {
        var attempts = 0;

        var run = () => TeamReasonWrites.RunAsync<bool>(
            "probe",
            token => DbUnitOfWork.OpenPlatformAsync(Contexts(Guid.Empty), token),
            (_, _) =>
            {
                attempts++;
                return Task.FromResult<(bool, TeamReasonChanges?)?>(null);
            },
            new GrantRevocationService(NewSessions()), Now, Ct);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*probe*");
        attempts.Should().Be(TeamReasonWrites.Attempts);
    }

    private async Task<Guid> TreatmentsRoleAsync(Site site) =>
        (await Members(site.Tenant).CreateRoleAsync(
            new NewTenantRole(site.Tenant, "Treatments", $"treatments-{Guid.NewGuid():N}", null, [Scope.TreatmentsRead], IsSystem: false),
            Ct)).Id;
}
