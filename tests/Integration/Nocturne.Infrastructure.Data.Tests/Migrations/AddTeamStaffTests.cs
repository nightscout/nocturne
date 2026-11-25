using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamStaff</c> on a real PostgreSQL as the migrator over a database holding a team, its
/// manager, a manager invite and the team's access to a site, then exercises the checks, the keys, the
/// cascades and the row level security of what it adds. A second database runs it up and back down.
/// </summary>
public class AddTeamStaffFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261123104146_AddTeamMembershipReasonSource";

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingTeam { get; } = Guid.CreateVersion7();

    internal Guid ExistingManager { get; private set; }

    internal Guid ExistingAccess { get; } = Guid.CreateVersion7();

    internal Guid ExistingInvite { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_staff");
        Migrator = database.MigratorConnectionString;

        ExistingManager = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{ExistingManager}', 'staff-{ExistingManager:N}', true, false, now(), now(), 'Approved');
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'staff-{tenant:N}', 'Site', true, now(), now());
            INSERT INTO teams (id, name, created_by_subject_id) VALUES ('{ExistingTeam}', 'Before', '{ExistingManager}');
            INSERT INTO team_members (id, team_id, subject_id, kind)
            VALUES ('{Guid.CreateVersion7()}', '{ExistingTeam}', '{ExistingManager}', 'manager');
            INSERT INTO team_invites (id, team_id, kind, created_by_subject_id, token_hash, expires_at, use_count)
            VALUES ('{ExistingInvite}', '{ExistingTeam}', 'manager', '{ExistingManager}', '{Guid.NewGuid():N}', now() + interval '1 day', 0);
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{ExistingAccess}', '{ExistingTeam}', '{tenant}', false);
            """);

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_staff_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<Guid> NewSubjectAsync()
    {
        var subject = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'staff-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewTeamAsync()
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');");
        return team;
    }

    internal async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'staff-{tenant:N}', 'Site', true, now(), now());
            """);
        return tenant;
    }

    internal async Task<Guid> NewAccessAsync(Guid team, Guid tenant)
    {
        var access = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{access}', '{team}', '{tenant}', false);
            """);
        return access;
    }

    internal async Task<Guid> NewStaffInviteAsync(Guid team, Guid createdBy)
    {
        var invite = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_staff_invites (id, team_id, created_by_subject_id, token_hash, expires_at, use_count, limit_to_24_hours)
            VALUES ('{invite}', '{team}', '{createdBy}', '{Guid.NewGuid():N}', now() + interval '1 day', 0, false);
            """);
        return invite;
    }

    internal async Task<Guid> NewMemberAsync(Guid team, Guid subject, string kind = "staff", string? setClause = null)
    {
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ('{member}', '{team}', '{subject}', '{kind}');
            {(setClause is null ? string.Empty : $"UPDATE team_members SET {setClause} WHERE id = '{member}';")}
            """);
        return member;
    }

    internal Task<int> NewSiteStaffAsync(Guid access, Guid member, string status = "approved") => NpgsqlTestCommands.ExecuteAsync(
        Migrator, $"""
        INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
        VALUES ('{Guid.CreateVersion7()}', '{access}', '{member}', '{status}');
        """);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamStaffTests(AddTeamStaffFixture fixture) : IClassFixture<AddTeamStaffFixture>
{
    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    [Fact]
    public async Task An_existing_manager_keeps_the_kind_and_holds_no_staff_narrowing()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM team_members
             WHERE team_id = '{fixture.ExistingTeam}' AND subject_id = '{fixture.ExistingManager}' AND kind = 'manager'
               AND role_ceiling_slug IS NULL AND NOT limit_to_24_hours AND ends_at IS NULL AND created_from_staff_invite_id IS NULL
            """)).Should().Be(1);
    }

    [Fact]
    public async Task An_existing_manager_invite_is_kept()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_invites WHERE id = '{fixture.ExistingInvite}' AND kind = 'manager'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task An_existing_access_is_kept_with_the_team_managing_its_staff_and_no_staff_rows()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM team_tenant_access WHERE id = '{fixture.ExistingAccess}' AND team_manages_staff
            """)).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_tenant_access_id = '{fixture.ExistingAccess}'")).Should().Be(0);
    }

    [Fact]
    public async Task A_member_of_an_unknown_kind_is_refused()
    {
        var team = await fixture.NewTeamAsync();
        var subject = await fixture.NewSubjectAsync();

        await ShouldViolateAsync(() => fixture.NewMemberAsync(team, subject, "owner"), PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("role_ceiling_slug = 'viewer'")]
    [InlineData("limit_to_24_hours = true")]
    [InlineData("ends_at = now() + interval '1 day'")]
    public async Task A_manager_holding_any_staff_narrowing_is_refused(string setClause)
    {
        var team = await fixture.NewTeamAsync();
        var subject = await fixture.NewSubjectAsync();

        await ShouldViolateAsync(() => fixture.NewMemberAsync(team, subject, "manager", setClause), PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_manager_that_joined_by_a_staff_invite_is_refused()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var invite = await fixture.NewStaffInviteAsync(team, maker);
        var subject = await fixture.NewSubjectAsync();

        await ShouldViolateAsync(
            () => fixture.NewMemberAsync(team, subject, "manager", $"created_from_staff_invite_id = '{invite}'"),
            PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_staff_member_holds_a_ceiling_a_limit_an_end_and_the_invite_they_joined_by()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var invite = await fixture.NewStaffInviteAsync(team, maker);
        var subject = await fixture.NewSubjectAsync();

        var member = await fixture.NewMemberAsync(
            team, subject, "staff",
            $"role_ceiling_slug = 'viewer', limit_to_24_hours = true, ends_at = now() + interval '1 day', created_from_staff_invite_id = '{invite}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_members WHERE id = '{member}' AND kind = 'staff'")).Should().Be(1);
    }

    [Fact]
    public async Task A_subject_is_on_a_team_once_whatever_the_kind()
    {
        var team = await fixture.NewTeamAsync();
        var subject = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(team, subject, "staff");

        await ShouldViolateAsync(() => fixture.NewMemberAsync(team, subject, "manager"), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_manager_invite_of_the_staff_kind_is_refused()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();

        await ShouldViolateAsync(
            () => NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"""
                INSERT INTO team_invites (id, team_id, kind, created_by_subject_id, token_hash, expires_at, use_count)
                VALUES ('{Guid.CreateVersion7()}', '{team}', 'staff', '{maker}', '{Guid.NewGuid():N}', now() + interval '1 day', 0);
                """),
            PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_staff_invite_token_is_unique()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var token = Guid.NewGuid().ToString("N");
        string Insert() => $"""
            INSERT INTO team_staff_invites (id, team_id, created_by_subject_id, token_hash, expires_at, use_count, limit_to_24_hours)
            VALUES ('{Guid.CreateVersion7()}', '{team}', '{maker}', '{token}', now() + interval '1 day', 0, false);
            """;
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, Insert());

        await ShouldViolateAsync(() => NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, Insert()), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_site_staff_row_of_an_unknown_status_is_refused()
    {
        var team = await fixture.NewTeamAsync();
        var access = await fixture.NewAccessAsync(team, await fixture.NewTenantAsync());
        var member = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());

        await ShouldViolateAsync(() => fixture.NewSiteStaffAsync(access, member, "waiting"), PostgresErrorCodes.CheckViolation);
        await fixture.NewSiteStaffAsync(access, member, "pending");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_member_id = '{member}'")).Should().Be(1);
    }

    [Fact]
    public async Task A_staff_member_has_one_site_staff_row_per_access()
    {
        var team = await fixture.NewTeamAsync();
        var access = await fixture.NewAccessAsync(team, await fixture.NewTenantAsync());
        var member = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());
        await fixture.NewSiteStaffAsync(access, member);

        await ShouldViolateAsync(() => fixture.NewSiteStaffAsync(access, member, "pending"), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Site_staff_rows_go_with_the_staff_member_the_access_and_the_team()
    {
        var team = await fixture.NewTeamAsync();
        var access = await fixture.NewAccessAsync(team, await fixture.NewTenantAsync());
        var leaving = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());
        var staying = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());
        await fixture.NewSiteStaffAsync(access, leaving);
        await fixture.NewSiteStaffAsync(access, staying);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_members WHERE id = '{leaving}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_tenant_access_id = '{access}'")).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_tenant_access WHERE id = '{access}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_member_id = '{staying}'")).Should().Be(0);

        var other = await fixture.NewAccessAsync(team, await fixture.NewTenantAsync());
        await fixture.NewSiteStaffAsync(other, staying);
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_tenant_access_id = '{other}'")).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_the_staff_invite_keeps_the_member_and_forgets_it_and_deleting_the_team_removes_both()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var invite = await fixture.NewStaffInviteAsync(team, maker);
        var member = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync(), "staff", $"created_from_staff_invite_id = '{invite}'");

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_staff_invites WHERE id = '{invite}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_members WHERE id = '{member}' AND created_from_staff_invite_id IS NULL"))
            .Should().Be(1);

        var second = await fixture.NewStaffInviteAsync(team, maker);
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_staff_invites WHERE id = '{second}'")).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_the_maker_of_a_staff_invite_removes_it()
    {
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var invite = await fixture.NewStaffInviteAsync(team, maker);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{maker}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_staff_invites WHERE id = '{invite}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_site_staff_rows_are_under_the_tenant_pin_and_the_staff_invites_are_not()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname = 'team_site_staff' AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'tenant_pin')
            """)).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, "SELECT count(*) FROM pg_class WHERE relname = 'team_staff_invites' AND NOT relrowsecurity"))
            .Should().Be(1);
    }

    [Fact]
    public async Task The_teams_policy_reads_for_every_member_and_writes_for_managers_only()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_policies
             WHERE tablename = 'teams' AND policyname = 'subject_pin'
               AND qual NOT LIKE '%manager%' AND with_check LIKE '%manager%'
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Down_drops_the_tables_the_columns_and_the_checks_and_restores_the_teams_policy()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'public' AND table_name IN ('team_staff_invites', 'team_site_staff')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'public'
               AND ((table_name = 'team_members' AND column_name IN ('role_ceiling_slug', 'limit_to_24_hours', 'ends_at', 'created_from_staff_invite_id'))
                 OR (table_name = 'team_tenant_access' AND column_name = 'team_manages_staff'))
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM pg_constraint
             WHERE conname IN ('ck_team_members_kind', 'ck_team_members_staff_columns', 'ck_team_invites_kind')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM pg_policies
             WHERE tablename = 'teams' AND policyname = 'subject_pin' AND with_check NOT LIKE '%manager%'
            """)).Should().Be(1);
    }
}
