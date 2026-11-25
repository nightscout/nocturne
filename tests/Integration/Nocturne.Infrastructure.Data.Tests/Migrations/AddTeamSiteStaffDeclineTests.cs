using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamSiteStaffDecline</c> on a real PostgreSQL as the migrator over a database holding a
/// team, its access to a site and an approved staff member there, then exercises the widened status,
/// the declines' key, cascades and row level security. A second database runs it up and back down.
/// </summary>
public class AddTeamSiteStaffDeclineFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261124225956_RecordImportStarter";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingAccess { get; } = Guid.CreateVersion7();

    internal Guid ExistingMember { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_site_decline");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;

        var subject = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'decline-{subject:N}', true, false, now(), now(), 'Approved');
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'decline-{tenant:N}', 'Site', true, now(), now());
            INSERT INTO teams (id, name) VALUES ('{team}', 'Before');
            INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ('{ExistingMember}', '{team}', '{subject}', 'staff');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{ExistingAccess}', '{team}', '{tenant}', false);
            INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
            VALUES ('{Guid.CreateVersion7()}', '{ExistingAccess}', '{ExistingMember}', 'approved');
            """);

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_site_decline_down");
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
            VALUES ('{subject}', 'decline-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<(Guid Tenant, Guid Team, Guid Access)> NewAccessAsync()
    {
        var tenant = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'decline-{tenant:N}', 'Site', true, now(), now());
            INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{access}', '{team}', '{tenant}', false);
            """);
        return (tenant, team, access);
    }

    internal async Task<Guid> NewMemberAsync(Guid team, Guid subject)
    {
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(
            Migrator, $"INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ('{member}', '{team}', '{subject}', 'staff');");
        return member;
    }

    internal Task<int> NewSiteStaffAsync(Guid access, Guid member, string status) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
        VALUES ('{Guid.CreateVersion7()}', '{access}', '{member}', '{status}');
        """);

    internal Task<int> NewDeclineAsync(Guid access, Guid subject) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_site_declines (id, team_tenant_access_id, subject_id)
        VALUES ('{Guid.CreateVersion7()}', '{access}', '{subject}');
        """);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamSiteStaffDeclineTests(AddTeamSiteStaffDeclineFixture fixture) : IClassFixture<AddTeamSiteStaffDeclineFixture>
{
    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    [Fact]
    public async Task An_existing_site_staff_row_keeps_its_status_and_no_decline_is_made()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM team_site_staff WHERE team_member_id = '{fixture.ExistingMember}' AND status = 'approved'
            """)).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_declines WHERE team_tenant_access_id = '{fixture.ExistingAccess}'")).Should().Be(0);
    }

    [Fact]
    public async Task A_site_staff_row_may_be_declined_and_no_other_new_status()
    {
        var (_, team, access) = await fixture.NewAccessAsync();
        var declined = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());
        var other = await fixture.NewMemberAsync(team, await fixture.NewSubjectAsync());

        await fixture.NewSiteStaffAsync(access, declined, "declined");

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_staff WHERE team_member_id = '{declined}' AND status = 'declined'"))
            .Should().Be(1);
        await ShouldViolateAsync(() => fixture.NewSiteStaffAsync(access, other, "refused"), PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_subject_has_one_decline_per_access()
    {
        var (_, _, access) = await fixture.NewAccessAsync();
        var subject = await fixture.NewSubjectAsync();
        await fixture.NewDeclineAsync(access, subject);

        await ShouldViolateAsync(() => fixture.NewDeclineAsync(access, subject), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_decline_outlasts_the_team_member_and_goes_with_the_access_or_the_subject()
    {
        var (_, team, access) = await fixture.NewAccessAsync();
        var leaving = await fixture.NewSubjectAsync();
        var deleted = await fixture.NewSubjectAsync();
        var member = await fixture.NewMemberAsync(team, leaving);
        await fixture.NewSiteStaffAsync(access, member, "declined");
        await fixture.NewDeclineAsync(access, leaving);
        await fixture.NewDeclineAsync(access, deleted);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_members WHERE id = '{member}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_declines WHERE subject_id = '{leaving}'")).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{deleted}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_declines WHERE team_tenant_access_id = '{access}'")).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM team_tenant_access WHERE id = '{access}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_declines WHERE team_tenant_access_id = '{access}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_declines_are_under_the_tenant_pin_and_a_context_pinned_to_another_site_reads_none()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname = 'team_site_declines' AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'tenant_pin')
            """)).Should().Be(1);

        var (tenant, _, access) = await fixture.NewAccessAsync();
        var (other, _, _) = await fixture.NewAccessAsync();
        await fixture.NewDeclineAsync(access, await fixture.NewSubjectAsync());

        (await PinnedCountAsync(tenant, access)).Should().Be(1);
        (await PinnedCountAsync(other, access)).Should().Be(0);
    }

    [Fact]
    public async Task A_context_pinned_to_one_site_may_write_a_decline_for_its_own_access_and_not_for_another_sites()
    {
        var (tenant, _, access) = await fixture.NewAccessAsync();
        var (_, _, otherAccess) = await fixture.NewAccessAsync();
        var subject = await fixture.NewSubjectAsync();

        await PinnedInsertAsync(tenant, access, subject);
        await ShouldViolateAsync(() => PinnedInsertAsync(tenant, otherAccess, subject), PostgresErrorCodes.InsufficientPrivilege);

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_declines WHERE subject_id = '{subject}'")).Should().Be(1);
    }

    private async Task<long> PinnedCountAsync(Guid site, Guid access)
    {
        await using var conn = await PinnedAsync(site);
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM team_site_declines WHERE team_tenant_access_id = '{access}'", conn);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }

    private async Task PinnedInsertAsync(Guid site, Guid access, Guid subject)
    {
        await using var conn = await PinnedAsync(site);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO team_site_declines (id, team_tenant_access_id, subject_id)
            VALUES ('{Guid.CreateVersion7()}', '{access}', '{subject}')
            """, conn);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Guid site)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        await using var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{site}', false)", conn);
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    [Fact]
    public async Task Down_drops_the_declines_and_restores_the_status_check()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'team_site_declines'
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM pg_constraint
             WHERE conname = 'ck_team_site_staff_status' AND pg_get_constraintdef(oid) NOT LIKE '%declined%'
            """)).Should().Be(1);
    }
}
