using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamAccess</c> on a real PostgreSQL as the migrator over a database holding a team,
/// then exercises the keys, the cascades and the row level security of the tables it creates. A
/// second database runs it up and back down.
/// </summary>
public class AddTeamAccessFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261122160931_AddTenantSetupOwnerSubject";

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingTeam { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_access");
        Migrator = database.MigratorConnectionString;
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{ExistingTeam}', 'Before');");

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_access_down");
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
            VALUES ('{subject}', 'access-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'access-{tenant:N}', 'Site', true, now(), now());
            """);
        return tenant;
    }

    internal async Task<Guid> NewRoleAsync(Guid tenant)
    {
        var role = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
            VALUES ('{role}', '{tenant}', 'Nights', 'nights-{role:N}', '["glucose.read"]', false, now(), now());
            """);
        return role;
    }

    internal async Task<Guid> NewTeamAsync()
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');");
        return team;
    }

    internal async Task<Guid> NewLinkAsync(Guid team, Guid createdBy)
    {
        var link = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_access_links (id, team_id, created_by_subject_id, token_hash, expires_at, use_count, suggested_role_slug, suggested_limit_to_24_hours)
            VALUES ('{link}', '{team}', '{createdBy}', '{Guid.NewGuid():N}', now() + interval '1 day', 0, 'caretaker', false);
            """);
        return link;
    }

    internal async Task<Guid> NewAccessAsync(Guid team, Guid tenant, Guid? approver = null, Guid? link = null)
    {
        var access = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours, approved_by_subject_id, created_from_link_id)
            VALUES ('{access}', '{team}', '{tenant}', false, {Sql(approver)}, {Sql(link)});
            """);
        return access;
    }

    internal Task<int> NewAccessRoleAsync(Guid access, Guid role) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_tenant_access_roles (id, team_tenant_access_id, tenant_role_id)
        VALUES ('{Guid.CreateVersion7()}', '{access}', '{role}');
        """);

    private static string Sql(Guid? id) => id is { } value ? $"'{value}'" : "NULL";

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamAccessTests(AddTeamAccessFixture fixture) : IClassFixture<AddTeamAccessFixture>
{
    [Fact]
    public async Task An_existing_team_is_kept_and_has_no_access()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM teams WHERE id = '{fixture.ExistingTeam}'")).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access WHERE team_id = '{fixture.ExistingTeam}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_access_and_its_roles_are_under_the_tenant_pin_and_the_links_are_not()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname IN ('team_tenant_access', 'team_tenant_access_roles') AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'tenant_pin')
            """)).Should().Be(2);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, "SELECT count(*) FROM pg_class WHERE relname = 'team_access_links' AND NOT relrowsecurity"))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_team_has_one_access_per_site_and_an_access_names_a_role_once()
    {
        var team = await fixture.NewTeamAsync();
        var tenant = await fixture.NewTenantAsync();
        var role = await fixture.NewRoleAsync(tenant);
        var access = await fixture.NewAccessAsync(team, tenant);
        await fixture.NewAccessRoleAsync(access, role);

        var secondAccess = () => fixture.NewAccessAsync(team, tenant);
        var secondRole = () => fixture.NewAccessRoleAsync(access, role);

        (await secondAccess.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        (await secondRole.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Deleting_a_team_removes_its_links_its_access_and_the_accesss_roles()
    {
        var subject = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var tenant = await fixture.NewTenantAsync();
        var role = await fixture.NewRoleAsync(tenant);
        var link = await fixture.NewLinkAsync(team, subject);
        var access = await fixture.NewAccessAsync(team, tenant, subject, link);
        await fixture.NewAccessRoleAsync(access, role);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_access_links WHERE team_id = '{team}'")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access WHERE team_id = '{team}'")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access_roles WHERE team_tenant_access_id = '{access}'")).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_site_removes_the_teams_access_to_it()
    {
        var team = await fixture.NewTeamAsync();
        var tenant = await fixture.NewTenantAsync();
        var access = await fixture.NewAccessAsync(team, tenant);
        await fixture.NewAccessRoleAsync(access, await fixture.NewRoleAsync(tenant));

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenants WHERE id = '{tenant}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access WHERE id = '{access}'")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access_roles WHERE team_tenant_access_id = '{access}'")).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_the_approver_or_the_link_keeps_the_access_and_forgets_them()
    {
        var approver = await fixture.NewSubjectAsync();
        var maker = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var tenant = await fixture.NewTenantAsync();
        var link = await fixture.NewLinkAsync(team, maker);
        var access = await fixture.NewAccessAsync(team, tenant, approver, link);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{approver}'");
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_access_links WHERE id = '{link}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM team_tenant_access
             WHERE id = '{access}' AND approved_by_subject_id IS NULL AND created_from_link_id IS NULL
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_role_removes_it_from_an_access()
    {
        var team = await fixture.NewTeamAsync();
        var tenant = await fixture.NewTenantAsync();
        var role = await fixture.NewRoleAsync(tenant);
        var access = await fixture.NewAccessAsync(team, tenant);
        await fixture.NewAccessRoleAsync(access, role);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenant_roles WHERE id = '{role}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access_roles WHERE team_tenant_access_id = '{access}'")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_tenant_access WHERE id = '{access}'")).Should().Be(1);
    }

    [Fact]
    public async Task Down_drops_the_tables_and_their_policies()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'public' AND table_name IN ('team_access_links', 'team_tenant_access', 'team_tenant_access_roles')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_policies WHERE tablename IN ('team_tenant_access', 'team_tenant_access_roles')")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'teams'")).Should().Be(1);
    }
}
