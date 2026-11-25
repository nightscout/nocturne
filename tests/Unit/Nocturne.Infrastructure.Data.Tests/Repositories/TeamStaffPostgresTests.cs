using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A team's staff through <see cref="TeamRepository"/> on PostgreSQL as the runtime role: the staff
/// invite, the member it makes, the <c>team_site_staff</c> row each staff member has for each site the
/// team follows, what a manager edits, and what each kind of member reads. No path here gives a staff
/// member a membership of a site.
/// </summary>
[Trait("Category", "Integration")]
public class TeamStaffPostgresTests(TeamStaffPostgresTests.Database database)
    : IClassFixture<TeamStaffPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("team_staff");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(Guid Id, IReadOnlyDictionary<string, Guid> Roles);

    private static readonly DateTime Now = WholeMicroseconds(DateTime.UtcNow);

    private static readonly TeamStaffNarrowing Everything = new(null, false, null);

    private static DateTime WholeMicroseconds(DateTime instant) =>
        new(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    private NocturneDbContext Context(IAuditContext? actor = null, Guid? tenant = null, bool retry = true) =>
        TestContexts.Context(
            database.AppConnectionString, tenant ?? Guid.Empty, npgsql: retry ? TestContexts.RetryingBriefly : null).ActingAs(actor);

    private SuppliedContextFactory Contexts(Guid? actor = null) =>
        new(() => Context(actor is { } id ? new TestAuditActor { SubjectId = id } : null));

    private TeamRepository Teams(Guid? actor = null) => new(Contexts(actor));

    private TenantMembershipRepository Members() => new(Contexts(), AccountLinkRoutes.Off);

    private SubjectRepository Subjects() => new(Contexts(), AccountLinkRoutes.Off);

    private Task<Guid> NewSubjectAsync(string name = "person") =>
        Subjects().CreateAsync(new NewSubject($"{name}-{Guid.NewGuid():N}"), default);

    private async Task<Site> NewSiteAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'Site', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"team-staff-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        await Members().SeedRolesAsync(tenant, default);
        return new Site(tenant, (await Members().ListRolesAsync(tenant, default)).ToDictionary(r => r.Slug, r => r.Id));
    }

    private async Task OwnerOfAsync(Site site, Guid subject)
    {
        var owner = (await Members().FindRoleIdBySlugAsync(site.Id, RoleSeeds.Owner, default))!.Value;
        (await Members().AddMemberAsync(
            new NewTenantMember(site.Id, subject, [owner]) { How = MembershipJoinHow.Direct, By = null }, default)).Should().BeTrue();
    }

    private async Task<(Guid Team, Guid Manager)> TeamAsync()
    {
        var manager = await NewSubjectAsync("manager");
        return ((await Teams(manager).CreateAsync(manager, "Camp", default)).Id, manager);
    }

    private async Task<string> AccessLinkAsync(Guid team, Guid manager)
    {
        var token = Guid.NewGuid().ToString("N");
        (await Teams(manager).CreateAccessLinkAsync(team, manager, new NewTeamAccessLink(
            token, null, Now.AddDays(1), null, new TeamAccessSuggestion(RoleSeeds.Caretaker, false, null)), default)).Should().NotBeNull();
        return token;
    }

    private static TeamAccessApproverCredential Credential(Guid subject) =>
        new(subject, AuthType.SessionCookie, new HashSet<string>(), false);

    private static TeamAccessGrant Grant(Site site, bool teamManagesStaff) =>
        new(site.Id, [site.Roles[RoleSeeds.Viewer]], false, null, teamManagesStaff);

    /// <summary>Gives the team access to a new site, as a new owner of it.</summary>
    private async Task<(Site Site, Guid Owner)> FollowAsync(Guid team, Guid manager, bool teamManagesStaff = true)
    {
        var site = await NewSiteAsync();
        var owner = await NewSubjectAsync("owner");
        await OwnerOfAsync(site, owner);
        (await TeamWrites.ApproveAccessAsync(
            Contexts(owner), await AccessLinkAsync(team, manager), Credential(owner), Grant(site, teamManagesStaff), Now)).Refusal.Should().BeNull();
        return (site, owner);
    }

    private async Task<(string Token, Guid InviteId)> StaffInviteAsync(
        Guid team, Guid manager, TeamStaffNarrowing? narrowing = null, int? maxUses = null, DateTime? expiresAt = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var id = await Teams(manager).CreateStaffInviteAsync(
            team, manager, new NewTeamStaffInvite(token, "Night shift", expiresAt ?? Now.AddDays(1), maxUses, narrowing ?? Everything), default);
        return (token, id!.Value);
    }

    private async Task<Guid> JoinStaffAsync(Guid team, Guid manager, TeamStaffNarrowing? narrowing = null)
    {
        var staff = await NewSubjectAsync("staff");
        var (token, _) = await StaffInviteAsync(team, manager, narrowing);
        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Refusal.Should().BeNull();
        return staff;
    }

    private async Task<List<(Guid Tenant, Guid Subject, string Status)>> SiteStaffAsync(Guid team)
    {
        await using var db = Context();
        var rows = await db.TeamSiteStaff.AsNoTracking()
            .Where(s => s.TeamTenantAccess!.TeamId == team)
            .Select(s => new { s.TeamTenantAccess!.TenantId, s.TeamMember!.SubjectId, s.Status })
            .ToListAsync();
        return rows.Select(r => (Tenant: r.TenantId, Subject: r.SubjectId, r.Status)).OrderBy(r => r.Subject).ThenBy(r => r.Tenant).ToList();
    }

    private async Task ApproveEverySiteRowAsync(Guid team)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE team_site_staff SET status = 'approved'
             WHERE team_tenant_access_id IN (SELECT id FROM team_tenant_access WHERE team_id = {team})
            """);
    }

    private async Task<List<Guid>> AccessIdsAsync(Guid team)
    {
        await using var db = Context();
        return await db.TeamTenantAccess.Where(a => a.TeamId == team).Select(a => a.Id).ToListAsync();
    }

    private async Task<int> SiteStaffRowsOfAsync(IEnumerable<Guid> accessIds)
    {
        var ids = accessIds.ToList();
        await using var db = Context();
        return await db.TeamSiteStaff.CountAsync(s => ids.Contains(s.TeamTenantAccessId));
    }

    private async Task<TeamMemberEntity> MemberAsync(Guid team, Guid subject)
    {
        await using var db = Context();
        return await db.TeamMembers.AsNoTracking().SingleAsync(m => m.TeamId == team && m.SubjectId == subject);
    }

    private async Task<TeamStaffInviteEntity> StoredInviteAsync(Guid invite)
    {
        await using var db = Context();
        return await db.TeamStaffInvites.AsNoTracking().SingleAsync(i => i.Id == invite);
    }

    private async Task<List<AuthAuditLogEntity>> AuditOfAsync(Guid team, params string[] eventTypes)
    {
        await using var db = Context();
        return await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TeamId == team && eventTypes.Contains(a.EventType))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync();
    }

    private static JsonElement Details(AuthAuditLogEntity row) => JsonDocument.Parse(row.DetailsJson!).RootElement;

    private static string Iso(DateTime instant) => instant.ToString("O", CultureInfo.InvariantCulture);

    private async Task<long> RowsOfSubjectAsync(Guid tenant, Guid subject)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString) { Pooling = false };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using (var pin = conn.CreateCommand())
        {
            pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false)";
            pin.Parameters.Add(new NpgsqlParameter("@tenant", tenant.ToString()));
            await pin.ExecuteScalarAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT count(*) FROM tenant_members WHERE subject_id = @subject AND tenant_id = @site)
                 + (SELECT count(*) FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id
                     WHERE m.subject_id = @subject AND m.tenant_id = @site)
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@subject", subject));
        cmd.Parameters.Add(new NpgsqlParameter("@site", tenant));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private Task UntilWaitingAsync(int sessions, params Task[] contenders) =>
        PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, sessions, contenders);

    [Fact]
    public async Task A_manager_makes_a_staff_invite_which_they_read_without_its_token_and_which_is_audited()
    {
        var (team, manager) = await TeamAsync();
        var end = Now.AddDays(10);
        var narrowing = new TeamStaffNarrowing(RoleSeeds.Clinician, true, end);

        var (token, id) = await StaffInviteAsync(team, manager, narrowing, maxUses: 3);

        var stored = await StoredInviteAsync(id);
        stored.TokenHash.Should().Be(token);
        (stored.RoleCeilingSlug, stored.LimitTo24Hours, stored.StaffEndsAt).Should().Be((RoleSeeds.Clinician, true, end));
        var summary = (await Teams().GetAsync(team, manager, Now, default))!.StaffInvites.Should().ContainSingle().Subject;
        summary.Id.Should().Be(id);
        summary.Label.Should().Be("Night shift");
        summary.MaxUses.Should().Be(3);
        summary.CreatedBySubjectId.Should().Be(manager);
        summary.Narrowing.Should().Be(narrowing);
        var row = (await AuditOfAsync(team, AuthAuditEventType.TeamStaffInviteCreated)).Should().ContainSingle().Subject;
        (row.ActorSubjectId, row.TargetSubjectId, row.TenantId).Should().Be((manager, (Guid?)null, (Guid?)null));
        Details(row).GetProperty("invite").GetString().Should().Be(id.ToString());
        Details(row).GetProperty("kind").GetString().Should().Be("team");
    }

    [Fact]
    public async Task A_manager_revokes_a_staff_invite_once_and_it_is_audited()
    {
        var (team, manager) = await TeamAsync();
        var staff = await NewSubjectAsync();
        var (token, id) = await StaffInviteAsync(team, manager);

        (await Teams(manager).RevokeStaffInviteAsync(team, manager, id, Now, default)).Should().BeTrue();
        (await Teams(manager).RevokeStaffInviteAsync(team, manager, id, Now, default)).Should().BeTrue();

        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await Teams().GetAsync(team, manager, Now, default))!.StaffInvites.Should().BeEmpty();
        var row = (await AuditOfAsync(team, AuthAuditEventType.TeamStaffInviteRevoked)).Should().ContainSingle().Subject;
        (row.ActorSubjectId, row.TargetSubjectId).Should().Be((manager, (Guid?)null));
        Details(row).GetProperty("invite").GetString().Should().Be(id.ToString());
    }

    [Fact]
    public async Task A_staff_invite_is_read_by_its_holder_with_the_team_the_maker_and_what_it_gives()
    {
        var (team, manager) = await TeamAsync();
        var holder = await NewSubjectAsync();
        var end = Now.AddDays(5);
        var narrowing = new TeamStaffNarrowing(RoleSeeds.Viewer, true, end);
        var (token, _) = await StaffInviteAsync(team, manager, narrowing);

        var info = await Teams().GetStaffInviteInfoAsync(token, holder, Now, default);

        info.Should().BeEquivalentTo(new { TeamId = team, TeamName = "Camp", Narrowing = narrowing, Refusal = (TeamInviteRefusal?)null });
        info!.CreatedByName.Should().StartWith("manager-");
        (await Teams().GetStaffInviteInfoAsync("no-such-token", holder, Now, default)).Should().BeNull();
    }

    [Fact]
    public async Task Accepting_a_staff_invite_makes_the_person_staff_with_what_it_gives_takes_a_use_and_is_audited()
    {
        var (team, manager) = await TeamAsync();
        var staff = await NewSubjectAsync("staff");
        var end = Now.AddDays(5);
        var (token, invite) = await StaffInviteAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end), maxUses: 2);

        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Should().Be(new TeamInviteAcceptance(team, null));

        var member = await MemberAsync(team, staff);
        (member.Kind, member.RoleCeilingSlug, member.LimitTo24Hours, member.EndsAt, member.CreatedFromStaffInviteId)
            .Should().Be((TeamMemberKind.Staff, RoleSeeds.Viewer, true, end, (Guid?)invite));
        (await StoredInviteAsync(invite)).UseCount.Should().Be(1);
        (await Teams().GetTeamsOfAsync(staff, default)).Should().Equal(new TeamSummary(team, "Camp", TeamMemberKind.Staff));
        var row = (await AuditOfAsync(team, AuthAuditEventType.TeamStaffAdded)).Should().ContainSingle().Subject;
        (row.ActorSubjectId, row.TargetSubjectId, row.TenantId).Should().Be((staff, (Guid?)staff, (Guid?)null));
        Details(row).GetProperty("invite").GetString().Should().Be(invite.ToString());
        Details(row).GetProperty("kind").GetString().Should().Be("team");
    }

    [Fact]
    public async Task A_staff_invite_with_no_narrowing_makes_staff_who_hold_everything_the_team_holds()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);

        var member = await MemberAsync(team, staff);

        (member.RoleCeilingSlug, member.LimitTo24Hours, member.EndsAt).Should().Be(((string?)null, false, (DateTime?)null));
    }

    [Fact]
    public async Task An_expired_staff_invite_adds_no_one()
    {
        var (team, manager) = await TeamAsync();
        var staff = await NewSubjectAsync();
        var (token, invite) = await StaffInviteAsync(team, manager, expiresAt: Now.AddMinutes(-1));

        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Refusal.Should().Be(TeamInviteRefusal.Expired);

        (await Teams().GetTeamsOfAsync(staff, default)).Should().BeEmpty();
        (await StoredInviteAsync(invite)).UseCount.Should().Be(0);
    }

    [Fact]
    public async Task A_used_up_staff_invite_adds_no_one()
    {
        var (team, manager) = await TeamAsync();
        var first = await NewSubjectAsync();
        var second = await NewSubjectAsync();
        var (token, invite) = await StaffInviteAsync(team, manager, maxUses: 1);
        (await TeamWrites.JoinStaffAsync(Contexts(first), token, first, Now)).Refusal.Should().BeNull();

        (await TeamWrites.JoinStaffAsync(Contexts(second), token, second, Now)).Refusal.Should().Be(TeamInviteRefusal.UsedUp);

        (await Teams().GetTeamsOfAsync(second, default)).Should().BeEmpty();
        (await StoredInviteAsync(invite)).UseCount.Should().Be(1);
    }

    [Fact]
    public async Task A_staff_invite_whose_maker_no_longer_manages_the_team_adds_no_one()
    {
        var (team, _) = await TeamAsync();
        var maker = await NewSubjectAsync();
        var invitee = await NewSubjectAsync();
        var token = Guid.NewGuid().ToString("N");
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ({Guid.CreateVersion7()}, {team}, {maker}, 'manager')");
        }

        await Teams(maker).CreateStaffInviteAsync(team, maker, new NewTeamStaffInvite(token, null, Now.AddDays(1), null, Everything), default);
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync($"DELETE FROM team_members WHERE team_id = {team} AND subject_id = {maker}");

        (await Teams().GetStaffInviteInfoAsync(token, invitee, Now, default))!.Refusal.Should().Be(TeamInviteRefusal.NoLongerValid);
        (await TeamWrites.JoinStaffAsync(Contexts(invitee), token, invitee, Now)).Refusal.Should().Be(TeamInviteRefusal.NoLongerValid);
        (await Teams().GetTeamsOfAsync(invitee, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_manager_who_opens_a_staff_invite_is_told_they_are_already_a_member_and_stays_a_manager()
    {
        var (team, manager) = await TeamAsync();
        var (token, invite) = await StaffInviteAsync(team, manager);

        (await TeamWrites.JoinStaffAsync(Contexts(manager), token, manager, Now)).Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);
        (await Teams().GetStaffInviteInfoAsync(token, manager, Now, default))!.Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);

        (await MemberAsync(team, manager)).Kind.Should().Be(TeamMemberKind.Manager);
        (await StoredInviteAsync(invite)).UseCount.Should().Be(0);
    }

    [Fact]
    public async Task A_staff_member_who_opens_a_staff_invite_again_is_told_they_are_already_a_member()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        var (token, invite) = await StaffInviteAsync(team, manager);

        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);

        (await MemberAsync(team, staff)).RoleCeilingSlug.Should().Be(RoleSeeds.Viewer, "the second invite changes nothing");
        (await StoredInviteAsync(invite)).UseCount.Should().Be(0);
    }

    [Fact]
    public async Task A_staff_member_who_opens_a_manager_invite_is_told_they_are_already_a_member_and_stays_staff()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);
        var token = Guid.NewGuid().ToString("N");
        await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(token, null, Now.AddDays(1), null), default);

        (await Teams(staff).AcceptInviteAsync(token, staff, Now, default)).Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);
        (await Teams().GetInviteInfoAsync(token, staff, Now, default))!.Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);

        (await MemberAsync(team, staff)).Kind.Should().Be(TeamMemberKind.Staff);
    }

    [Fact]
    public async Task A_staff_invite_whose_end_has_passed_adds_no_one()
    {
        var (team, manager) = await TeamAsync();
        var staff = await NewSubjectAsync();
        var (token, invite) = await StaffInviteAsync(team, manager, new TeamStaffNarrowing(null, false, Now.AddHours(1)));
        var later = Now.AddHours(2);

        (await Teams().GetStaffInviteInfoAsync(token, staff, later, default))!.Refusal.Should().Be(TeamInviteRefusal.StaffEndPassed);
        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, later)).Refusal.Should().Be(TeamInviteRefusal.StaffEndPassed);

        (await Teams().GetTeamsOfAsync(staff, default)).Should().BeEmpty();
        (await StoredInviteAsync(invite)).UseCount.Should().Be(0);
        (await Teams().GetAsync(team, manager, later, default))!.StaffInvites.Should().BeEmpty("it can give nothing now");
    }

    [Fact]
    public async Task A_manager_token_is_not_found_by_the_staff_accept_and_a_staff_token_by_the_manager_accept()
    {
        var (team, manager) = await TeamAsync();
        var person = await NewSubjectAsync();
        var managerToken = Guid.NewGuid().ToString("N");
        await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(managerToken, null, Now.AddDays(1), null), default);
        var (staffToken, _) = await StaffInviteAsync(team, manager);

        (await TeamWrites.JoinStaffAsync(Contexts(person), managerToken, person, Now)).Refusal.Should().Be(TeamInviteRefusal.NotFound);
        (await Teams(person).AcceptInviteAsync(staffToken, person, Now, default)).Refusal.Should().Be(TeamInviteRefusal.NotFound);
        (await Teams().GetStaffInviteInfoAsync(managerToken, person, Now, default)).Should().BeNull();
        (await Teams().GetInviteInfoAsync(staffToken, person, Now, default)).Should().BeNull();

        (await Teams().GetTeamsOfAsync(person, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Joining_a_team_with_two_sites_gives_one_approved_row_where_the_team_manages_staff_and_one_pending_where_it_does_not()
    {
        var (team, manager) = await TeamAsync();
        var (managed, _) = await FollowAsync(team, manager, teamManagesStaff: true);
        var (decided, _) = await FollowAsync(team, manager, teamManagesStaff: false);

        var staff = await JoinStaffAsync(team, manager);

        (await SiteStaffAsync(team)).Should().BeEquivalentTo(
            [(managed.Id, staff, TeamSiteStaffStatus.Approved), (decided.Id, staff, TeamSiteStaffStatus.Pending)]);
    }

    [Fact]
    public async Task A_site_approving_a_team_with_two_staff_gives_each_a_row_with_the_status_of_its_switch()
    {
        var (team, manager) = await TeamAsync();
        var first = await JoinStaffAsync(team, manager);
        var second = await JoinStaffAsync(team, manager);
        (await SiteStaffAsync(team)).Should().BeEmpty("the team follows no site yet");

        var (managed, _) = await FollowAsync(team, manager, teamManagesStaff: true);
        var (decided, _) = await FollowAsync(team, manager, teamManagesStaff: false);

        (await SiteStaffAsync(team)).Should().BeEquivalentTo(
        [
            (managed.Id, first, TeamSiteStaffStatus.Approved), (managed.Id, second, TeamSiteStaffStatus.Approved),
            (decided.Id, first, TeamSiteStaffStatus.Pending), (decided.Id, second, TeamSiteStaffStatus.Pending),
        ]);
    }

    [Fact]
    public async Task A_switch_stated_off_in_the_grant_is_stored_off_despite_the_column_default_of_on()
    {
        var (team, manager) = await TeamAsync();
        var (site, _) = await FollowAsync(team, manager, teamManagesStaff: false);

        await using var db = Context();

        (await db.TeamTenantAccess.SingleAsync(a => a.TeamId == team && a.TenantId == site.Id)).TeamManagesStaff.Should().BeFalse();
    }

    [Fact]
    public async Task A_row_is_written_even_when_the_staff_member_owns_the_site_at_the_join_and_at_the_approval()
    {
        var (team, manager) = await TeamAsync();
        var staff = await NewSubjectAsync("staff");
        var followed = await NewSiteAsync();
        var owner = await NewSubjectAsync("owner");
        await OwnerOfAsync(followed, owner);
        (await TeamWrites.ApproveAccessAsync(
            Contexts(owner), await AccessLinkAsync(team, manager), Credential(owner), Grant(followed, true), Now)).Refusal.Should().BeNull();
        await OwnerOfAsync(followed, staff);
        var (token, _) = await StaffInviteAsync(team, manager);
        (await TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now)).Refusal.Should().BeNull();

        var later = await NewSiteAsync();
        await OwnerOfAsync(later, staff);
        (await TeamWrites.ApproveAccessAsync(
            Contexts(staff), await AccessLinkAsync(team, manager), Credential(staff), Grant(later, false), Now)).Refusal.Should().BeNull();

        (await SiteStaffAsync(team)).Should().BeEquivalentTo(
            [(followed.Id, staff, TeamSiteStaffStatus.Approved), (later.Id, staff, TeamSiteStaffStatus.Pending)]);
    }

    [Fact]
    public async Task A_staff_member_who_leaves_takes_their_site_rows_and_nobody_elses()
    {
        var (team, manager) = await TeamAsync();
        var (site, _) = await FollowAsync(team, manager);
        var leaving = await JoinStaffAsync(team, manager);
        var staying = await JoinStaffAsync(team, manager);

        (await TeamWrites.RemoveStaffAsync(Contexts(leaving), team, leaving, leaving)).Should().BeTrue();

        (await SiteStaffAsync(team)).Should().Equal((site.Id, staying, TeamSiteStaffStatus.Approved));
        (await Teams().GetTeamsOfAsync(leaving, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_manager_who_removes_a_staff_member_takes_their_site_rows()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager);
        var staff = await JoinStaffAsync(team, manager);

        (await TeamWrites.RemoveStaffAsync(Contexts(manager), team, manager, staff)).Should().BeTrue();

        (await SiteStaffAsync(team)).Should().BeEmpty();
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_a_sites_access_takes_that_sites_rows_and_keeps_the_others()
    {
        var (team, manager) = await TeamAsync();
        var (stopped, _) = await FollowAsync(team, manager);
        var (kept, _) = await FollowAsync(team, manager);
        var staff = await JoinStaffAsync(team, manager);

        (await TeamWrites.StopAccessAsync(Contexts(), stopped.Id, team)).Should().BeTrue();

        (await SiteStaffAsync(team)).Should().Equal((kept.Id, staff, TeamSiteStaffStatus.Approved));
    }

    [Fact]
    public async Task Deleting_the_team_takes_every_site_row()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager);
        await FollowAsync(team, manager);
        await JoinStaffAsync(team, manager);
        var accesses = await AccessIdsAsync(team);
        (await SiteStaffRowsOfAsync(accesses)).Should().Be(2);

        (await TeamWrites.DeleteAsync(Contexts(manager), team, manager)).Should().Be(TeamDeletion.Deleted);

        (await SiteStaffRowsOfAsync(accesses)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_staff_members_account_takes_their_membership_and_their_site_rows()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager);
        var staff = await JoinStaffAsync(team, manager);
        var accesses = await AccessIdsAsync(team);

        (await Subjects().DeleteAsync(staff, default)).Should().BeOfType<SubjectChange.Changed>();

        (await SiteStaffRowsOfAsync(accesses)).Should().Be(0);
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Should().BeEmpty();
    }

    [Fact]
    public async Task A_join_and_an_approval_at_once_leave_one_row_for_the_pair()
    {
        var (team, manager) = await TeamAsync();
        var site = await NewSiteAsync();
        var owner = await NewSubjectAsync("owner");
        await OwnerOfAsync(site, owner);
        var link = await AccessLinkAsync(team, manager);
        var (token, _) = await StaffInviteAsync(team, manager);
        var staff = await NewSubjectAsync("staff");

        await using var holder = Context(retry: false);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockTeamManagersAsync(team, default);

        var join = TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now);
        var approval = TeamWrites.ApproveAccessAsync(Contexts(owner), link, Credential(owner), Grant(site, true), Now);
        await UntilWaitingAsync(2, join, approval);
        await held.RollbackAsync();

        (await join.WaitAsync(Eventually.DefaultPatience)).Refusal.Should().BeNull();
        (await approval.WaitAsync(Eventually.DefaultPatience)).Refusal.Should().BeNull();
        (await SiteStaffAsync(team)).Should().Equal((site.Id, staff, TeamSiteStaffStatus.Approved));
    }

    [Fact]
    public async Task A_join_waiting_on_a_stop_of_a_site_writes_rows_only_for_the_sites_the_team_still_follows()
    {
        var (team, manager) = await TeamAsync();
        var (stopped, _) = await FollowAsync(team, manager);
        var (kept, _) = await FollowAsync(team, manager);
        var (token, _) = await StaffInviteAsync(team, manager);
        var staff = await NewSubjectAsync("staff");

        await using var stop = Context(tenant: stopped.Id, retry: false);
        await using var held = await stop.Database.BeginTransactionAsync();
        await stop.LockTeamAccessAsync(stopped.Id, default);
        await stop.Database.ExecuteSqlAsync($"DELETE FROM team_tenant_access WHERE team_id = {team} AND tenant_id = {stopped.Id}");

        var join = TeamWrites.JoinStaffAsync(Contexts(staff), token, staff, Now);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, join);
        await held.CommitAsync();

        (await join.WaitAsync(Eventually.DefaultPatience)).Refusal.Should().BeNull();
        (await SiteStaffAsync(team)).Should().Equal((kept.Id, staff, TeamSiteStaffStatus.Approved));
    }

    [Fact]
    public async Task A_staff_member_holds_a_team_reason_at_a_site_that_approved_them_and_nothing_where_they_wait()
    {
        var (team, manager) = await TeamAsync();
        var (before, _) = await FollowAsync(team, manager, teamManagesStaff: true);
        var staff = await JoinStaffAsync(team, manager);
        var (after, _) = await FollowAsync(team, manager, teamManagesStaff: false);

        (await SiteStaffAsync(team)).Select(r => (r.Tenant, r.Status)).Should().BeEquivalentTo(
            [(before.Id, TeamSiteStaffStatus.Approved), (after.Id, TeamSiteStaffStatus.Pending)]);
        (await RowsOfSubjectAsync(before.Id, staff)).Should().Be(2, "the membership row and its one team reason");
        (await Members().IsMemberAsync(before.Id, staff, Now, default)).Should().BeTrue();
        (await RowsOfSubjectAsync(after.Id, staff)).Should().Be(0);
        (await Members().IsMemberAsync(after.Id, staff, Now, default)).Should().BeFalse();

        (await Members().ListMembershipsOfSubjectAsync(staff, Now, default)).Select(m => m.Tenant.Id).Should().Equal(before.Id);
    }

    [Fact]
    public async Task A_manager_changes_the_ceiling_the_limit_and_the_end_and_each_is_read_back()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);
        var end = Now.AddDays(3);

        (await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Clinician, false, null), Now)).Should().Be(TeamStaffChange.Changed);
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Single().Narrowing.Should().Be(new TeamStaffNarrowing(RoleSeeds.Clinician, false, null));

        (await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Clinician, true, null), Now)).Should().Be(TeamStaffChange.Changed);
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Single().Narrowing.Should().Be(new TeamStaffNarrowing(RoleSeeds.Clinician, true, null));

        (await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Clinician, true, end), Now)).Should().Be(TeamStaffChange.Changed);
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Single().Narrowing.Should().Be(new TeamStaffNarrowing(RoleSeeds.Clinician, true, end));
    }

    [Fact]
    public async Task An_edit_is_audited_with_the_old_and_the_new_ceiling_limit_and_end()
    {
        var (team, manager) = await TeamAsync();
        var start = Now.AddDays(2);
        var end = Now.AddDays(9);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Caretaker, false, start));

        await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end), Now);

        var row = (await AuditOfAsync(team, AuthAuditEventType.TeamStaffChanged)).Should().ContainSingle().Subject;
        (row.ActorSubjectId, row.TargetSubjectId, row.TenantId).Should().Be((manager, (Guid?)staff, (Guid?)null));
        var details = Details(row);
        details.GetProperty("old_ceiling").GetString().Should().Be(RoleSeeds.Caretaker);
        details.GetProperty("ceiling").GetString().Should().Be(RoleSeeds.Viewer);
        details.GetProperty("old_history_limit").GetString().Should().Be("full");
        details.GetProperty("history_limit").GetString().Should().Be("24_hours");
        details.GetProperty("old_ends_at").GetString().Should().Be(Iso(start));
        details.GetProperty("ends_at").GetString().Should().Be(Iso(end));
        details.GetProperty("kind").GetString().Should().Be("team");
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_no_audit_row()
    {
        var (team, manager) = await TeamAsync();
        var narrowing = new TeamStaffNarrowing(RoleSeeds.Viewer, true, Now.AddDays(2));
        var staff = await JoinStaffAsync(team, manager, narrowing);

        (await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, narrowing, Now)).Should().Be(TeamStaffChange.Changed);

        (await AuditOfAsync(team, AuthAuditEventType.TeamStaffChanged)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_widening_edit_sends_the_approvals_a_site_decides_back_to_pending_and_leaves_the_teams_own_alone()
    {
        var (team, manager) = await TeamAsync();
        var (managed, _) = await FollowAsync(team, manager, teamManagesStaff: true);
        var (decided, _) = await FollowAsync(team, manager, teamManagesStaff: false);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, true, Now.AddDays(2)));
        await ApproveEverySiteRowAsync(team);

        await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, Now.AddDays(2)), Now);

        (await SiteStaffAsync(team)).Should().BeEquivalentTo(
            [(managed.Id, staff, TeamSiteStaffStatus.Approved), (decided.Id, staff, TeamSiteStaffStatus.Pending)]);
    }

    [Fact]
    public async Task A_widening_edit_waiting_on_a_stop_of_one_of_two_sites_is_saved_and_leaves_the_other_site_pending()
    {
        var (team, manager) = await TeamAsync();
        var (stopped, _) = await FollowAsync(team, manager, teamManagesStaff: false);
        var (kept, _) = await FollowAsync(team, manager, teamManagesStaff: false);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, true, null));
        await ApproveEverySiteRowAsync(team);

        await using var stop = Context(tenant: stopped.Id, retry: false);
        await using var held = await stop.Database.BeginTransactionAsync();
        await stop.LockTeamAccessAsync(stopped.Id, default);
        await stop.Database.ExecuteSqlAsync($"DELETE FROM team_tenant_access WHERE team_id = {team} AND tenant_id = {stopped.Id}");

        var edit = TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null), Now);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, edit);
        await held.CommitAsync();

        (await edit.WaitAsync(Eventually.DefaultPatience)).Should().Be(TeamStaffChange.Changed);
        (await SiteStaffAsync(team)).Should().Equal((kept.Id, staff, TeamSiteStaffStatus.Pending));
        (await MemberAsync(team, staff)).LimitTo24Hours.Should().BeFalse();
        (await AuditOfAsync(team, AuthAuditEventType.TeamStaffChanged)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_staff_member_whose_end_has_passed_is_narrowed_with_the_end_unchanged_and_no_approval_changes()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager, teamManagesStaff: false);
        var end = Now.AddHours(1);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(null, false, end));
        await ApproveEverySiteRowAsync(team);

        var result = await TeamWrites.UpdateStaffAsync(
            Contexts(manager), team, manager, staff, new TeamStaffNarrowing(null, true, end), Now.AddHours(2));

        result.Should().Be(TeamStaffChange.Changed);
        var member = await MemberAsync(team, staff);
        (member.LimitTo24Hours, member.EndsAt).Should().Be((true, (DateTime?)end));
        (await SiteStaffAsync(team)).Should().OnlyContain(r => r.Status == TeamSiteStaffStatus.Approved);
    }

    [Fact]
    public async Task An_end_moved_to_another_passed_instant_is_refused_and_changes_nothing()
    {
        var (team, manager) = await TeamAsync();
        var end = Now.AddHours(1);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(null, false, end));

        var result = await TeamWrites.UpdateStaffAsync(
            Contexts(manager), team, manager, staff, new TeamStaffNarrowing(null, true, Now.AddMinutes(30)), Now.AddHours(2));

        result.Should().Be(TeamStaffChange.EndPassed);
        var member = await MemberAsync(team, staff);
        (member.LimitTo24Hours, member.EndsAt).Should().Be((false, (DateTime?)end));
        (await AuditOfAsync(team, AuthAuditEventType.TeamStaffChanged)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_end_of_a_staff_member_moved_to_the_future_is_accepted_and_is_a_widening()
    {
        var (team, manager) = await TeamAsync();
        var (decided, _) = await FollowAsync(team, manager, teamManagesStaff: false);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(null, false, Now.AddHours(1)));
        await ApproveEverySiteRowAsync(team);
        var later = Now.AddHours(2);
        var future = later.AddDays(1);

        var result = await TeamWrites.UpdateStaffAsync(
            Contexts(manager), team, manager, staff, new TeamStaffNarrowing(null, false, future), later);

        result.Should().Be(TeamStaffChange.Changed);
        (await MemberAsync(team, staff)).EndsAt.Should().Be(future);
        (await SiteStaffAsync(team)).Should().Equal((decided.Id, staff, TeamSiteStaffStatus.Pending));
    }

    [Fact]
    public async Task A_narrowing_edit_changes_no_approval()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager, teamManagesStaff: true);
        var (decided, _) = await FollowAsync(team, manager, teamManagesStaff: false);
        var staff = await JoinStaffAsync(team, manager);
        await ApproveEverySiteRowAsync(team);

        await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, true, Now.AddDays(2)), Now);

        (await SiteStaffAsync(team)).Should().OnlyContain(r => r.Status == TeamSiteStaffStatus.Approved);
        (await SiteStaffAsync(team)).Should().Contain(r => r.Tenant == decided.Id);
    }

    [Fact]
    public async Task A_manager_reads_each_staff_member_with_the_sites_approved_and_pending_and_whether_their_end_has_passed()
    {
        var (team, manager) = await TeamAsync();
        await FollowAsync(team, manager, teamManagesStaff: true);
        await FollowAsync(team, manager, teamManagesStaff: false);
        await FollowAsync(team, manager, teamManagesStaff: false);
        var end = Now.AddHours(1);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end));

        var current = (await Teams().GetAsync(team, manager, Now, default))!.Staff.Should().ContainSingle().Subject;
        var ended = (await Teams().GetAsync(team, manager, Now.AddHours(2), default))!.Staff.Should().ContainSingle().Subject;

        current.Should().Be(new TeamStaffMember(staff, current.DisplayName, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end), false, 1, 2));
        current.DisplayName.Should().StartWith("staff-");
        ended.HasEnded.Should().BeTrue();
    }

    [Fact]
    public async Task A_staff_member_reads_the_teams_name_the_managers_names_what_narrows_them_and_the_role_names_and_nothing_else()
    {
        var (team, manager) = await TeamAsync();
        var second = await NewSubjectAsync("second");
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ({Guid.CreateVersion7()}, {team}, {second}, 'manager')");
        var (site, _) = await FollowAsync(team, manager);
        var end = Now.AddDays(4);
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end));
        var other = await JoinStaffAsync(team, manager);
        var (_, managerInvite) = await ManagerInviteAsync(team, manager);
        var (_, staffInvite) = await StaffInviteAsync(team, manager);
        await AccessLinkAsync(team, manager);

        var detail = await Teams().GetAsync(team, staff, Now, default);

        detail!.Name.Should().Be("Camp");
        detail.CallerKind.Should().Be(TeamMemberKind.Staff);
        detail.OwnStaff.Should().Be(new TeamStaffSelf(new TeamStaffNarrowing(RoleSeeds.Viewer, true, end), false));
        detail.ManagerNames.Should().HaveCount(2).And.OnlyContain(n => n.StartsWith("manager-") || n.StartsWith("second-"));
        detail.Managers.Should().BeEmpty();
        detail.Invites.Should().BeEmpty();
        detail.StaffInvites.Should().BeEmpty();
        detail.AccessLinks.Should().BeEmpty();
        detail.Sites.Should().BeEmpty();
        detail.Staff.Should().BeEmpty();
        detail.SuggestableRoles.Should().BeSameAs(TeamSuggestableRoles.All);
        (detail.IsLastManager, detail.CanDelete).Should().Be((false, false));
        var json = JsonSerializer.Serialize(detail);
        foreach (var secret in new[] { manager, second, other, site.Id, managerInvite, staffInvite })
            json.Should().NotContain(secret.ToString());
    }

    [Fact]
    public async Task A_manager_reads_the_managers_with_their_ids_and_no_own_staff_narrowing()
    {
        var (team, manager) = await TeamAsync();

        var detail = await Teams().GetAsync(team, manager, Now, default);

        detail!.Managers.Select(m => m.SubjectId).Should().Equal(manager);
        detail.ManagerNames.Should().Equal(detail.Managers.Select(m => m.DisplayName));
        detail.OwnStaff.Should().BeNull();
    }

    [Fact]
    public async Task A_staff_member_whose_end_has_passed_stays_on_the_team_and_in_their_list_stated_as_ended()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager, new TeamStaffNarrowing(null, false, Now.AddHours(1)));
        var later = Now.AddHours(2);

        var detail = await Teams().GetAsync(team, staff, later, default);

        detail!.OwnStaff!.HasEnded.Should().BeTrue();
        (await Teams().GetTeamsOfAsync(staff, default)).Should().Equal(new TeamSummary(team, "Camp", TeamMemberKind.Staff));
        (await Teams().GetAsync(team, manager, later, default))!.Staff.Single().HasEnded.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_member_reads_and_changes_nothing_through_the_staff_routes()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);
        var stranger = await NewSubjectAsync("stranger");
        var (_, invite) = await StaffInviteAsync(team, manager);
        var teams = Teams(stranger);

        (await teams.CreateStaffInviteAsync(team, stranger, new NewTeamStaffInvite("x", null, Now.AddDays(1), null, Everything), default)).Should().BeNull();
        (await teams.RevokeStaffInviteAsync(team, stranger, invite, Now, default)).Should().BeFalse();
        (await TeamWrites.UpdateStaffAsync(Contexts(stranger), team, stranger, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, true, null), Now)).Should().Be(TeamStaffChange.NotFound);
        (await TeamWrites.RemoveStaffAsync(Contexts(stranger), team, stranger, staff)).Should().BeFalse();
        (await TeamWrites.RemoveStaffAsync(Contexts(stranger), team, stranger, stranger)).Should().BeFalse();

        (await MemberAsync(team, staff)).RoleCeilingSlug.Should().BeNull();
        (await StoredInviteAsync(invite)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_staff_member_cannot_run_the_team_or_edit_its_staff_and_can_leave()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);
        var peer = await JoinStaffAsync(team, manager);
        var (_, invite) = await StaffInviteAsync(team, manager);
        var managerToken = Guid.NewGuid().ToString("N");
        var managerInvite = await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(managerToken, null, Now.AddDays(1), null), default);
        var teams = Teams(staff);

        (await teams.RenameAsync(team, staff, "Mine", default)).Should().BeNull();
        (await TeamWrites.DeleteAsync(Contexts(staff), team, staff)).Should().Be(TeamDeletion.Refused);
        (await teams.CreateInviteAsync(team, staff, new NewTeamInvite("x", null, Now.AddDays(1), null), default)).Should().BeNull();
        (await teams.RevokeInviteAsync(team, staff, managerInvite!.Value, Now, default)).Should().BeFalse();
        (await teams.CreateStaffInviteAsync(team, staff, new NewTeamStaffInvite("y", null, Now.AddDays(1), null, Everything), default)).Should().BeNull();
        (await teams.RevokeStaffInviteAsync(team, staff, invite, Now, default)).Should().BeFalse();
        (await teams.CreateAccessLinkAsync(team, staff, new NewTeamAccessLink(
            "z", null, Now.AddDays(1), null, new TeamAccessSuggestion(RoleSeeds.Viewer, false, null)), default)).Should().BeNull();
        (await teams.RevokeAccessLinkAsync(team, staff, Guid.NewGuid(), Now, default)).Should().BeFalse();
        (await teams.RemoveManagerAsync(team, staff, manager, Now, default)).Should().Be(ManagerGuardedChange.NotFound);
        (await TeamWrites.UpdateStaffAsync(Contexts(staff), team, staff, peer, new TeamStaffNarrowing(RoleSeeds.Viewer, true, null), Now)).Should().Be(TeamStaffChange.NotFound);
        (await TeamWrites.UpdateStaffAsync(Contexts(staff), team, staff, staff, new TeamStaffNarrowing(null, false, null), Now)).Should().Be(TeamStaffChange.NotFound);
        (await TeamWrites.RemoveStaffAsync(Contexts(staff), team, staff, peer)).Should().BeFalse();

        (await Teams().GetAsync(team, manager, Now, default))!.Name.Should().Be("Camp");
        (await MemberAsync(team, peer)).Kind.Should().Be(TeamMemberKind.Staff);
        (await TeamWrites.RemoveStaffAsync(Contexts(staff), team, staff, staff)).Should().BeTrue();
        (await Teams().GetTeamsOfAsync(staff, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_staff_by_a_manager_id_or_a_manager_by_a_staff_id_finds_nothing()
    {
        var (team, manager) = await TeamAsync();
        var second = await NewSubjectAsync("second");
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ({Guid.CreateVersion7()}, {team}, {second}, 'manager')");
        var staff = await JoinStaffAsync(team, manager);

        (await TeamWrites.RemoveStaffAsync(Contexts(manager), team, manager, second)).Should().BeFalse();
        (await Teams(manager).RemoveManagerAsync(team, manager, staff, Now, default)).Should().Be(ManagerGuardedChange.NotFound);
        (await TeamWrites.UpdateStaffAsync(Contexts(manager), team, manager, second, new TeamStaffNarrowing(RoleSeeds.Viewer, true, null), Now)).Should().Be(TeamStaffChange.NotFound);

        (await MemberAsync(team, second)).Kind.Should().Be(TeamMemberKind.Manager);
        (await MemberAsync(team, staff)).Kind.Should().Be(TeamMemberKind.Staff);
    }

    [Fact]
    public async Task Removing_a_staff_member_is_audited_as_removed_by_a_manager_and_as_left_by_the_staff_member()
    {
        var (team, manager) = await TeamAsync();
        var removed = await JoinStaffAsync(team, manager);
        var left = await JoinStaffAsync(team, manager);

        await TeamWrites.RemoveStaffAsync(Contexts(manager), team, manager, removed);
        await TeamWrites.RemoveStaffAsync(Contexts(left), team, left, left);

        var rows = await AuditOfAsync(team, AuthAuditEventType.TeamStaffRemoved);
        rows.Select(r => (r.ActorSubjectId, r.TargetSubjectId, How: Details(r).GetProperty("how").GetString(), Kind: Details(r).GetProperty("kind").GetString()))
            .Should().Equal(((Guid?)manager, (Guid?)removed, "removed", "team"), (left, left, "left", "team"));
    }

    [Fact]
    public async Task With_one_manager_and_one_staff_member_the_manager_cannot_leave_and_their_account_cannot_be_deleted()
    {
        var (team, manager) = await TeamAsync();
        await JoinStaffAsync(team, manager);

        (await Teams(manager).RemoveManagerAsync(team, manager, manager, Now, default)).Should().Be(ManagerGuardedChange.LastManager);

        (await Subjects().DeleteAsync(manager, default)).Should().BeEquivalentTo(new SubjectChange.LastManagerOf([new LastManagerTeam(team, "Camp")]));
        (await Teams().GetAsync(team, manager, Now, default))!.Staff.Should().ContainSingle();
    }

    [Fact]
    public async Task Removing_staff_never_trips_the_last_manager_rule()
    {
        var (team, manager) = await TeamAsync();
        var staff = await JoinStaffAsync(team, manager);

        (await TeamWrites.RemoveStaffAsync(Contexts(manager), team, manager, staff)).Should().BeTrue();

        (await Teams().GetAsync(team, manager, Now, default))!.IsLastManager.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_manager_revokes_their_live_staff_invites_and_leaves_another_managers_alone()
    {
        var (team, a) = await TeamAsync();
        var r = await NewSubjectAsync("removed");
        var tokenOfR = Guid.NewGuid().ToString("N");
        var managerToken = Guid.NewGuid().ToString("N");
        await Teams(a).CreateInviteAsync(team, a, new NewTeamInvite(managerToken, null, Now.AddDays(1), null), default);
        (await Teams(r).AcceptInviteAsync(managerToken, r, Now, default)).Refusal.Should().BeNull();
        var theirs = (await Teams(r).CreateStaffInviteAsync(team, r, new NewTeamStaffInvite(tokenOfR, null, Now.AddDays(1), null, Everything), default))!.Value;
        var (kept, keptId) = await StaffInviteAsync(team, a);
        var newcomer = await NewSubjectAsync();

        (await Teams(a).RemoveManagerAsync(team, a, r, Now, default)).Should().Be(ManagerGuardedChange.Changed);

        (await TeamWrites.JoinStaffAsync(Contexts(newcomer), tokenOfR, newcomer, Now)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await StoredInviteAsync(theirs)).RevokedAt.Should().NotBeNull();
        (await StoredInviteAsync(keptId)).RevokedAt.Should().BeNull();
        (await TeamWrites.JoinStaffAsync(Contexts(newcomer), kept, newcomer, Now)).Refusal.Should().BeNull();
        var row = (await AuditOfAsync(team, AuthAuditEventType.TeamStaffInviteRevoked)).Should().ContainSingle().Subject;
        (row.ActorSubjectId, Details(row).GetProperty("invite").GetString()).Should().Be(((Guid?)a, theirs.ToString()));
    }

    private async Task<(string Token, Guid InviteId)> ManagerInviteAsync(Guid team, Guid manager)
    {
        var token = Guid.NewGuid().ToString("N");
        var id = await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(token, null, Now.AddDays(1), null), default);
        return (token, id!.Value);
    }
}
