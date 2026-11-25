using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The team writes that give team reasons: a staff member joining or being edited, a site approving the
/// team's access, a role the access gives being edited, and the startup check, each bringing the team
/// reasons of the staff they reach into line with the team's rows.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private const int Runs = 5;

    private sealed record Held(IReadOnlyList<Guid> RoleIds, IReadOnlyList<string>? DirectPermissions, bool LimitTo24Hours, DateTime? AccessEndsAt);

    private async Task<Held?> ReasonAsync(Site site, Guid subject, Guid access)
    {
        await using var db = Context(site.Tenant, null);
        var reason = await db.TenantMemberReasons.AsNoTracking()
            .Where(r => r.SourceId == access && r.TenantMember.TenantId == site.Tenant && r.TenantMember.SubjectId == subject)
            .Select(r => new { RoleIds = r.ReasonRoles.Select(rr => rr.TenantRoleId).ToList(), r.DirectPermissions, r.LimitTo24Hours, r.AccessEndsAt })
            .FirstOrDefaultAsync(Ct);
        return reason is null ? null : new Held(reason.RoleIds, reason.DirectPermissions, reason.LimitTo24Hours, reason.AccessEndsAt);
    }

    private async Task<int> AuditCountAsync(Site site) =>
        int.Parse((await ReadAsync($"SELECT count(*)::text FROM auth_audit_log WHERE tenant_id = '{site.Tenant}'")).Single());

    private Task ReconcileAsync() =>
        DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(
            database.MigratorConnectionString, Now,
            (unit, changes, ct) => TeamReasonWrites.ApplyAsync(unit, changes, new GrantRevocationService(NewSessions()), Now, ct),
            NullLogger.Instance, Ct);

    /// <summary>The sites where a sync of <paramref name="subject"/>'s memberships would change a team reason now.</summary>
    private async Task<IReadOnlyList<Guid>> OutOfLineAsync(Guid subject, params Site[] sites)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(Contexts(Guid.Empty), Ct);
        return await unit.CommitAsync(async ct =>
        {
            await unit.Context.LockSubjectRowSharedAsync(subject, ct);
            foreach (var site in sites.OrderBy(s => s.Tenant))
                await unit.Context.LockTeamAccessAsync(site.Tenant, ct);
            return (await TeamReasonChanges.SyncAsync(unit, sites.Select(s => (s.Tenant, subject)), Now, ct)).Sites;
        }, ct: Ct);
    }

    private async Task<Guid> NightRoleAsync(Site site) =>
        (await Members(site.Tenant).CreateRoleAsync(
            new NewTenantRole(site.Tenant, "Night", $"night-{Guid.NewGuid():N}", null, [Scope.GlucoseRead, Scope.TreatmentsRead], IsSystem: false),
            Ct)).Id;

    private static TeamAccessGrant Gives(Site site, IReadOnlyList<Guid> roles, bool teamManagesStaff = true, bool limit = false, DateTime? end = null) =>
        new(site.Tenant, roles, limit, end, teamManagesStaff);

    private async Task ApproveSiteRowsAsync(Site site) =>
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE team_site_staff SET status = 'approved'
             WHERE team_tenant_access_id IN (SELECT id FROM team_tenant_access WHERE tenant_id = '{site.Tenant}');
            """);

    private static Task BothAsync(Func<Task> first, Func<Task> second) =>
        Task.WhenAll(Task.Run(first), Task.Run(second)).WaitAsync(Eventually.DefaultPatience);

    [Fact]
    public async Task Joining_gives_a_team_reason_where_the_team_decides_its_staff_and_none_where_the_site_does()
    {
        var decides = await SiteAsync("join-decides");
        var waits = await SiteAsync("join-waits");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, decides);
        var pending = await FollowAsync(team, waits, Gives(waits, [waits.ViewerRole], teamManagesStaff: false));
        var staff = await NewSubjectAsync("staff");

        (await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, null))).Refusal.Should().BeNull();

        (await ReasonAsync(decides, staff, access)).Should().BeEquivalentTo(new Held([decides.ViewerRole], null, false, null));
        await RowIsTheSumAsync(decides, staff);
        (await ReasonAsync(waits, staff, pending)).Should().BeNull();
        (await Members(waits.Tenant).FindMemberAsync(waits.Tenant, staff, Ct)).Should().BeNull();
        var joined = (await RowsAsync(decides.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberJoined).Subject;
        joined.Read(MemberAccessAudit.HowKey).Should().Be("team");
        joined.Team.Should().Be(team.Id);

        var audited = await AuditCountAsync(decides);
        (await OutOfLineAsync(staff, decides, waits)).Should().BeEmpty();
        await ReconcileAsync();
        (await AuditCountAsync(decides)).Should().Be(audited, "a second sync of what agrees writes nothing");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approving_gives_every_staff_member_the_team_approves_a_reason(bool teamManagesStaff)
    {
        var site = await SiteAsync($"approve-{teamManagesStaff}");
        var team = await TeamAsync("Clinic");
        var first = await JoinStaffAsync(team);
        var second = await JoinStaffAsync(team);

        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff));

        foreach (var staff in new[] { first, second })
        {
            if (teamManagesStaff)
                await HoldsTeamReasonAsync(site, staff, access);
            else
                (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        }

        (await OutOfLineAsync(first, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_ceiling_keeps_a_role_within_it_and_gives_of_a_wider_one_only_what_both_grant()
    {
        var site = await SiteAsync("meet");
        var team = await TeamAsync("Clinic");
        var night = await NightRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [night, site.CaretakerRole]));
        var clinician = await NewSubjectAsync("clinician");
        var viewer = await NewSubjectAsync("viewer");

        await JoinAsync(team, clinician, new TeamStaffNarrowing(RoleSeeds.Clinician, false, null));
        await JoinAsync(team, viewer, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));

        var clinicianHeld = (await ReasonAsync(site, clinician, access))!;
        clinicianHeld.RoleIds.Should().Equal(night);
        clinicianHeld.DirectPermissions.Should().BeEquivalentTo(RoleSeeds.Permissions[RoleSeeds.Clinician], "caretaker capped at clinician is clinician")
            .And.BeInAscendingOrder(StringComparer.Ordinal);
        (await ReasonAsync(site, viewer, access)).Should().BeEquivalentTo(new Held(
            [], [Scope.DeviceActuate, Scope.DeviceNotify, Scope.GlucoseRead, Scope.ReportsRead], false, null),
            options => options.WithStrictOrdering());
        await RowIsTheSumAsync(site, clinician);
        await RowIsTheSumAsync(site, viewer);
    }

    [Fact]
    public async Task The_limit_is_the_access_or_the_staff_members_and_the_end_is_the_earlier_of_theirs()
    {
        var site = await SiteAsync("limit-end");
        var team = await TeamAsync("Clinic");
        var accessEnd = Now.AddDays(10);
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], limit: true, end: accessEnd));
        var later = await NewSubjectAsync("later");
        var sooner = await NewSubjectAsync("sooner");
        var soonerEnd = Now.AddDays(3);

        await JoinAsync(team, later, new TeamStaffNarrowing(null, false, Now.AddDays(20)));
        await JoinAsync(team, sooner, new TeamStaffNarrowing(null, false, soonerEnd));

        (await ReasonAsync(site, later, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, true, accessEnd));
        (await ReasonAsync(site, sooner, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, true, soonerEnd));
    }

    [Fact]
    public async Task A_ceiling_no_seed_role_has_gives_nothing_and_the_check_ends_a_reason_that_stood_on_it()
    {
        var (site, _, access, staff) = await StaffWithReasonAsync("unknown-ceiling");
        var made = await MadeByAsync(site, staff);
        await NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_members SET role_ceiling_slug = 'retired' WHERE subject_id = '{staff}'");

        await ReconcileAsync();

        (await ReasonAsync(site, staff, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
    }

    [Fact]
    public async Task Narrowing_a_staff_member_lowers_the_reasons_end_and_caps_what_they_made_at_it()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("narrow");
        var made = await MadeByAsync(site, staff);
        var end = Now.AddDays(5);

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, true, end), Ct))
            .Should().Be(TeamStaffChange.Changed);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, true, end));
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.AccessEndsAt.Should().Be(end);
        await RowIsTheSumAsync(site, staff);
        (await StateAsync(site, made)).Should().Be(CappedAt(end));
    }

    [Fact]
    public async Task Widening_a_staff_member_where_the_site_decides_ends_the_reason_and_stops_what_they_made()
    {
        var site = await SiteAsync("widen");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        await ApproveSiteRowsAsync(site);
        await ReconcileAsync();
        await HoldsTeamReasonAsync(site, staff, access);
        var made = await MadeByAsync(site, staff);

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);

        (await ReasonAsync(site, staff, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
    }

    [Fact]
    public async Task Editing_a_role_the_access_gives_recomputes_what_a_capped_staff_member_holds_of_it()
    {
        var site = await SiteAsync("role-edit-capped");
        var team = await TeamAsync("Clinic");
        var night = await NightRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [night]));
        var staff = await NewSubjectAsync("staff");
        await JoinAsync(team, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        (await ReasonAsync(site, staff, access))!.DirectPermissions.Should().Equal(Scope.GlucoseRead);

        var edited = await TeamReasonWrites.EditRoleAsync(
            Contexts(site.Tenant, site.Owner), new GrantRevocationService(NewSessions()), site.Tenant, night,
            new TenantRoleEdit("Night", "night", null, [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead]), site.Owner, Now, Ct);

        edited.Outcome.Should().Be(RoleChangeOutcome.Changed);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(
            new Held([], [Scope.GlucoseRead, Scope.ReportsRead], false, null), options => options.WithStrictOrdering());
        await RowIsTheSumAsync(site, staff);
    }

    [Fact]
    public async Task A_role_given_whole_is_read_with_its_edit_and_the_reason_is_not_rewritten()
    {
        var site = await SiteAsync("role-edit-whole");
        var team = await TeamAsync("Clinic");
        var night = await NightRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [night]));
        var staff = await JoinStaffAsync(team);
        var audited = await AuditCountAsync(site);

        await TeamReasonWrites.EditRoleAsync(
            Contexts(site.Tenant, site.Owner), new GrantRevocationService(NewSessions()), site.Tenant, night,
            new TenantRoleEdit("Night", "night", null, [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead]), site.Owner, Now, Ct);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([night], null, false, null));
        (await AuditCountAsync(site)).Should().Be(audited + 1, "only the role's own change is recorded");
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.Roles.Single().Permissions
            .Should().BeEquivalentTo(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead);
    }

    [Fact]
    public async Task The_startup_check_gives_the_reason_of_a_row_approved_before_and_a_second_run_writes_nothing()
    {
        var site = await SiteAsync("backfill");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await JoinStaffAsync(team);
        await ApproveSiteRowsAsync(site);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();

        await ReconcileAsync();

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
        await RowIsTheSumAsync(site, staff);
        var audited = await AuditCountAsync(site);
        await ReconcileAsync();
        (await AuditCountAsync(site)).Should().Be(audited);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
    }

    [Fact]
    public async Task A_join_and_the_joiners_account_deletion_at_once_both_finish_and_leave_nothing_of_them()
    {
        for (var run = 0; run < Runs; run++)
        {
            var site = await SiteAsync($"race-join-delete-{run}");
            var team = await TeamAsync("Clinic");
            await FollowAsync(team, site);
            var staff = await NewSubjectAsync("staff");
            var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));

            await BothAsync(
                () => Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct),
                () => Deletion(staff).DeleteAsync(staff, false, Ct));

            (await new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off).GetAsync(staff, Ct)).Should().BeNull();
            (await ReadAsync($"SELECT count(*)::text FROM tenant_members WHERE subject_id = '{staff}'")).Should().Equal("0");
            (await ReadAsync($"SELECT count(*)::text FROM team_members WHERE subject_id = '{staff}'")).Should().Equal("0");
        }
    }

    [Fact]
    public async Task An_approval_and_a_staff_removal_at_once_both_finish_and_leave_the_removed_staff_member_nothing()
    {
        for (var run = 0; run < Runs; run++)
        {
            var site = await SiteAsync($"race-approve-remove-{run}");
            var team = await TeamAsync("Clinic");
            var staff = await JoinStaffAsync(team);
            var kept = await JoinStaffAsync(team);
            Guid? access = null;

            await BothAsync(
                async () => access = await FollowAsync(team, site),
                () => Service(Guid.Empty, team.Manager).RemoveStaffAsync(team.Id, team.Manager, staff, Ct));

            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
            (await ReadAsync($"SELECT count(*)::text FROM team_members WHERE subject_id = '{staff}'")).Should().Equal("0");
            await HoldsTeamReasonAsync(site, kept, access!.Value);
        }
    }

    [Fact]
    public async Task The_startup_check_and_a_stop_at_once_both_finish_and_leave_no_reason_on_the_stopped_access()
    {
        for (var run = 0; run < Runs; run++)
        {
            var site = await SiteAsync($"race-backfill-stop-{run}");
            var team = await TeamAsync("Clinic");
            var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
            var staff = await JoinStaffAsync(team);
            await ApproveSiteRowsAsync(site);

            await BothAsync(
                ReconcileAsync,
                () => Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct));

            (await AccessExistsAsync(access)).Should().BeFalse();
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
            (await ReadAsync($"SELECT count(*)::text FROM tenant_member_reasons WHERE source_id = '{access}'")).Should().Equal("0");
        }
    }
}
