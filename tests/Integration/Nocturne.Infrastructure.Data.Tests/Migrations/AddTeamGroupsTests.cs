using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamGroups</c> on a real PostgreSQL as the migrator over a team that existed before it, then
/// exercises the setting's default and check, the groups' keys and cascades, and row level security. A second
/// database runs it up and back down.
/// </summary>
public class AddTeamGroupsFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261126052603_AddAlertInstanceRenotifiedAt";

    internal static readonly Guid ExistingTeam = Guid.CreateVersion7();

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_groups");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{ExistingTeam}', 'Before');");
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_groups_down");
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
            VALUES ('{subject}', 'group-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewSiteAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.SeedTenantAsync(Migrator, tenant, $"group-{tenant:N}", "Site");
        return tenant;
    }

    internal async Task<Guid> NewTeamAsync()
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');");
        return team;
    }

    internal async Task<Guid> NewMemberAsync(Guid team, Guid subject, string kind)
    {
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours)
            VALUES ('{member}', '{team}', '{subject}', '{kind}', false);
            """);
        return member;
    }

    internal async Task<Guid> NewGroupAsync(Guid team, string name)
    {
        var group = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO team_groups (id, team_id, name) VALUES ('{group}', '{team}', '{name}');");
        return group;
    }

    internal Task<int> NewPersonAsync(Guid group, Guid tenant) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{tenant}');
        """);

    internal Task<int> NewStaffAsync(Guid group, Guid member) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_group_staff (id, team_group_id, team_member_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{member}');
        """);

    internal Task<long> CountAsync(string sql) => NpgsqlTestCommands.ScalarAsync<long>(Migrator, sql);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamGroupsTests(AddTeamGroupsFixture fixture) : IClassFixture<AddTeamGroupsFixture>
{
    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    [Fact]
    public async Task A_team_that_existed_reaches_every_person_and_no_other_value_is_stored()
    {
        (await NpgsqlTestCommands.ScalarAsync<string>(fixture.Migrator,
            $"SELECT staff_reach FROM teams WHERE id = '{AddTeamGroupsFixture.ExistingTeam}'")).Should().Be("every_person");

        var team = await fixture.NewTeamAsync();
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"UPDATE teams SET staff_reach = 'their_groups' WHERE id = '{team}'");
        await ShouldViolateAsync(
            () => NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"UPDATE teams SET staff_reach = 'nobody' WHERE id = '{team}'"),
            PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_group_name_is_taken_in_its_team_whatever_its_case_and_free_in_another_team()
    {
        var team = await fixture.NewTeamAsync();
        var other = await fixture.NewTeamAsync();
        await fixture.NewGroupAsync(team, "Ward A");

        await ShouldViolateAsync(() => fixture.NewGroupAsync(team, "ward a"), PostgresErrorCodes.UniqueViolation);
        await fixture.NewGroupAsync(other, "WARD A");
        (await fixture.CountAsync($"SELECT count(*) FROM team_groups WHERE name_key = 'ward a' AND team_id IN ('{team}', '{other}')"))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_site_and_a_staff_place_are_in_a_group_once()
    {
        var team = await fixture.NewTeamAsync();
        var group = await fixture.NewGroupAsync(team, "Ward");
        var site = await fixture.NewSiteAsync();
        var member = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync(), "staff");
        await fixture.NewPersonAsync(group, site);
        await fixture.NewStaffAsync(group, member);

        await ShouldViolateAsync(() => fixture.NewPersonAsync(group, site), PostgresErrorCodes.UniqueViolation);
        await ShouldViolateAsync(() => fixture.NewStaffAsync(group, member), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_group_place_goes_with_its_site_its_staff_place_its_subject_its_group_and_its_team()
    {
        var team = await fixture.NewTeamAsync();
        var group = await fixture.NewGroupAsync(team, "Ward");
        var site = await fixture.NewSiteAsync();
        var kept = await fixture.NewSiteAsync();
        var removed = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync(), "staff");
        var deletedSubject = await fixture.NewSubjectAsync();
        var deleted = await fixture.NewMemberAsync(team, deletedSubject, "staff");
        var stays = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync(), "staff");
        await fixture.NewPersonAsync(group, site);
        await fixture.NewPersonAsync(group, kept);
        await fixture.NewStaffAsync(group, removed);
        await fixture.NewStaffAsync(group, deleted);
        await fixture.NewStaffAsync(group, stays);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenants WHERE id = '{site}'");
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_members WHERE id = '{removed}'");
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{deletedSubject}'");
        (await fixture.CountAsync($"SELECT count(*) FROM team_group_people WHERE team_group_id = '{group}'")).Should().Be(1);
        (await fixture.CountAsync($"SELECT count(*) FROM team_group_staff WHERE team_group_id = '{group}'")).Should().Be(1);

        var second = await fixture.NewGroupAsync(team, "Clinic");
        await fixture.NewPersonAsync(second, kept);
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_groups WHERE id = '{group}'");
        (await fixture.CountAsync($"SELECT count(*) FROM team_group_staff WHERE team_group_id = '{group}'")).Should().Be(0);
        (await fixture.CountAsync($"SELECT count(*) FROM team_group_people WHERE tenant_id = '{kept}'")).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");
        (await fixture.CountAsync($"SELECT count(*) FROM team_groups WHERE team_id = '{team}'")).Should().Be(0);
        (await fixture.CountAsync($"SELECT count(*) FROM team_group_people WHERE tenant_id = '{kept}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_groups_are_under_the_subject_pin_and_their_sites_also_under_the_tenant_pin()
    {
        (await fixture.CountAsync("""
            SELECT count(*) FROM pg_class c
             WHERE c.relname IN ('team_groups', 'team_group_people', 'team_group_staff') AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'subject_pin' AND p.polpermissive)
            """)).Should().Be(3);
        (await fixture.CountAsync("""
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
             WHERE c.relname = 'team_group_people' AND p.polname = 'tenant_pin' AND NOT p.polpermissive
            """)).Should().Be(1);
    }

    [Fact]
    public async Task A_subject_pinned_context_reads_only_the_groups_of_its_teams()
    {
        var team = await fixture.NewTeamAsync();
        var other = await fixture.NewTeamAsync();
        var staff = await fixture.NewSubjectAsync();
        var member = await fixture.NewMemberAsync(team, staff, "staff");
        var group = await fixture.NewGroupAsync(team, "Ward");
        var otherGroup = await fixture.NewGroupAsync(other, "Ward");
        var site = await fixture.NewSiteAsync();
        await fixture.NewPersonAsync(group, site);
        await fixture.NewPersonAsync(otherGroup, site);
        await fixture.NewStaffAsync(group, member);
        await fixture.NewStaffAsync(otherGroup, await fixture.NewMemberAsync(other, await fixture.NewSubjectAsync(), "staff"));

        foreach (var table in new[] { "team_groups", "team_group_people", "team_group_staff" })
        {
            var scope = table == "team_groups" ? $"id IN ('{group}', '{otherGroup}')" : $"team_group_id IN ('{group}', '{otherGroup}')";
            (await ScalarAsync(Pin.Subject(staff), $"SELECT count(*) FROM {table} WHERE {scope}")).Should().Be(1, table);
            (await ScalarAsync(Pin.Subject(null), $"SELECT count(*) FROM {table} WHERE {scope}")).Should().Be(2, table);
        }
    }

    [Fact]
    public async Task A_subject_pinned_context_writes_groups_only_where_it_manages()
    {
        var team = await fixture.NewTeamAsync();
        var manager = await fixture.NewSubjectAsync();
        var staff = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(team, manager, "manager");
        await fixture.NewMemberAsync(team, staff, "staff");
        var group = await fixture.NewGroupAsync(team, "Ward");
        var site = await fixture.NewSiteAsync();
        var refused = await fixture.NewSiteAsync();

        await ExecuteAsync(Pin.Subject(manager), $"INSERT INTO team_groups (id, team_id, name) VALUES ('{Guid.CreateVersion7()}', '{team}', 'Clinic')");
        await ExecuteAsync(Pin.Subject(manager), $"INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{site}')");
        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Subject(staff), $"INSERT INTO team_groups (id, team_id, name) VALUES ('{Guid.CreateVersion7()}', '{team}', 'Other')"),
            PostgresErrorCodes.InsufficientPrivilege);
        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Subject(staff), $"INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{refused}')"),
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_tenant_pinned_context_reads_and_writes_only_its_own_site_in_a_group()
    {
        var team = await fixture.NewTeamAsync();
        var group = await fixture.NewGroupAsync(team, "Ward");
        var site = await fixture.NewSiteAsync();
        var other = await fixture.NewSiteAsync();
        await fixture.NewPersonAsync(group, site);
        await fixture.NewPersonAsync(group, other);

        var count = $"SELECT count(*) FROM team_group_people WHERE team_group_id = '{group}'";
        (await ScalarAsync(Pin.Tenant(site), count)).Should().Be(1);
        (await ScalarAsync(Pin.Tenant(null), count)).Should().Be(2);

        var second = await fixture.NewGroupAsync(team, "Clinic");
        await ExecuteAsync(Pin.Tenant(site), $"INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{second}', '{site}')");
        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Tenant(site), $"INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{second}', '{other}')"),
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Down_drops_the_groups_and_the_setting()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'public' AND table_name IN ('team_groups', 'team_group_people', 'team_group_staff')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns WHERE table_name = 'teams' AND column_name = 'staff_reach'
            """)).Should().Be(0);
    }

    private sealed record Pin(string Setting, Guid? Value)
    {
        public static Pin Subject(Guid? subject) => new("app.current_subject_id", subject);

        public static Pin Tenant(Guid? tenant) => new("app.current_tenant_id", tenant);
    }

    private async Task<long> ScalarAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Pin pin)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('{pin.Setting}', '{pin.Value?.ToString() ?? string.Empty}', false)", conn);
        await set.ExecuteNonQueryAsync();
        return conn;
    }
}
