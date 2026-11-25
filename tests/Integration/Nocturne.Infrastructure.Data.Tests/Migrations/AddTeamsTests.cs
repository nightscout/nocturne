using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeams</c> on a real PostgreSQL as the migrator, over a database holding a saved platform
/// policy and an audit row, then exercises the keys, the cascades and the row level security of the
/// tables it creates. A second database runs it up and back down.
/// </summary>
public class AddTeamsFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261122112333_AddTenantMemberReasons";

    internal static readonly Guid AuditRow = Guid.Parse("a1111111-1111-7111-8111-111111113842");

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_teams");
        Migrator = database.MigratorConnectionString;

        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO platform_policy (id, allow_owner_slug_change, sys_created_at, sys_updated_at)
            VALUES (1, true, now(), now());
            INSERT INTO auth_audit_log (id, event_type, success, created_at)
            VALUES ('{AuditRow}', 'login', true, now());
            """);

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_teams_down");
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
            VALUES ('{subject}', 'teams-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewTeamAsync(Guid createdBy, string name = "Camp staff")
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(
            Migrator, $"INSERT INTO teams (id, name, created_by_subject_id) VALUES ('{team}', '{name}', '{createdBy}');");
        return team;
    }

    internal Task<int> NewMemberAsync(Guid team, Guid subject) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ('{Guid.CreateVersion7()}', '{team}', '{subject}', 'manager');
        """);

    internal Task<int> NewInviteAsync(Guid team, Guid createdBy) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_invites (id, team_id, kind, created_by_subject_id, token_hash, expires_at, use_count)
        VALUES ('{Guid.CreateVersion7()}', '{team}', 'manager', '{createdBy}', '{Guid.NewGuid():N}', now() + interval '1 day', 0);
        """);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamsTests(AddTeamsFixture fixture) : IClassFixture<AddTeamsFixture>
{
    [Fact]
    public async Task A_saved_policy_keeps_its_switch_and_does_not_restrict_team_creation()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator,
                "SELECT count(*) FROM platform_policy WHERE id = 1 AND allow_owner_slug_change AND NOT restrict_team_creation_to_admins"))
            .Should().Be(1);
    }

    [Fact]
    public async Task An_existing_audit_row_names_no_team()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM auth_audit_log WHERE id = '{AddTeamsFixture.AuditRow}' AND team_id IS NULL"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Teams_and_their_members_are_under_the_subject_pin_and_invites_are_not()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname IN ('teams', 'team_members') AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'subject_pin')
            """)).Should().Be(2);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, "SELECT count(*) FROM pg_class WHERE relname = 'team_invites' AND NOT relrowsecurity"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Two_teams_may_share_a_name()
    {
        var subject = await fixture.NewSubjectAsync();

        await fixture.NewTeamAsync(subject, "Same name");
        var second = () => fixture.NewTeamAsync(subject, "Same name");

        await second.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_subject_is_a_member_of_a_team_once()
    {
        var subject = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync(subject);
        await fixture.NewMemberAsync(team, subject);

        var again = () => fixture.NewMemberAsync(team, subject);

        (await again.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Deleting_a_subject_removes_its_memberships_and_invites_and_keeps_the_team_it_created()
    {
        var creator = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync(creator);
        await fixture.NewMemberAsync(team, creator);
        await fixture.NewMemberAsync(team, other);
        await fixture.NewInviteAsync(team, creator);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{creator}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM teams WHERE id = '{team}' AND created_by_subject_id IS NULL")).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_members WHERE team_id = '{team}'")).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_invites WHERE team_id = '{team}'")).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_team_removes_its_members_and_invites()
    {
        var subject = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync(subject);
        await fixture.NewMemberAsync(team, subject);
        await fixture.NewInviteAsync(team, subject);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_members WHERE team_id = '{team}'")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_invites WHERE team_id = '{team}'")).Should().Be(0);
    }

    [Fact]
    public async Task Down_drops_the_tables_their_policies_and_the_columns()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'public' AND table_name IN ('teams', 'team_members', 'team_invites')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_policies WHERE tablename IN ('teams', 'team_members', 'team_invites')")).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE (table_name = 'platform_policy' AND column_name = 'restrict_team_creation_to_admins')
                OR (table_name = 'auth_audit_log' AND column_name = 'team_id')
            """)).Should().Be(0);
    }
}
