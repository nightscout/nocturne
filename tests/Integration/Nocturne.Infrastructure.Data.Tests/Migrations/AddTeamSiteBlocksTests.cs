using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamSiteBlocks</c> on a real PostgreSQL as the migrator, then exercises the stops' key, their
/// cascades and row level security. A second database runs it up and back down.
/// </summary>
public class AddTeamSiteBlocksFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261125210451_ReplaceTeamManagesStaffWithStaffRule";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_site_blocks");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_site_blocks_down");
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
            VALUES ('{subject}', 'block-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewSiteAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.SeedTenantAsync(Migrator, tenant, $"block-{tenant:N}", "Site");
        return tenant;
    }

    internal Task<int> NewBlockAsync(Guid tenant, Guid subject, Guid? createdBy) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_site_blocks (id, tenant_id, subject_id, created_by_subject_id)
        VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{subject}', {(createdBy is { } by ? $"'{by}'" : "NULL")});
        """);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamSiteBlocksTests(AddTeamSiteBlocksFixture fixture) : IClassFixture<AddTeamSiteBlocksFixture>
{
    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    [Fact]
    public async Task A_site_has_one_stop_per_subject_and_another_site_may_stop_the_same_subject()
    {
        var site = await fixture.NewSiteAsync();
        var other = await fixture.NewSiteAsync();
        var subject = await fixture.NewSubjectAsync();
        await fixture.NewBlockAsync(site, subject, null);

        await ShouldViolateAsync(() => fixture.NewBlockAsync(site, subject, null), PostgresErrorCodes.UniqueViolation);
        await fixture.NewBlockAsync(other, subject, null);
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_blocks WHERE subject_id = '{subject}'")).Should().Be(2);
    }

    [Fact]
    public async Task A_stop_goes_with_its_subject_or_its_site_and_outlasts_the_owner_who_set_it()
    {
        var site = await fixture.NewSiteAsync();
        var owner = await fixture.NewSubjectAsync();
        var stopped = await fixture.NewSubjectAsync();
        var deleted = await fixture.NewSubjectAsync();
        await fixture.NewBlockAsync(site, stopped, owner);
        await fixture.NewBlockAsync(site, deleted, owner);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{owner}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM team_site_blocks WHERE tenant_id = '{site}' AND created_by_subject_id IS NULL
            """)).Should().Be(2);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{deleted}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_blocks WHERE tenant_id = '{site}'")).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenants WHERE id = '{site}'");
        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_blocks WHERE subject_id = '{stopped}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_stops_are_under_the_tenant_pin_and_a_context_pinned_to_another_site_reads_none()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname = 'team_site_blocks' AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'tenant_pin')
            """)).Should().Be(1);

        var site = await fixture.NewSiteAsync();
        var other = await fixture.NewSiteAsync();
        var subject = await fixture.NewSubjectAsync();
        await fixture.NewBlockAsync(site, subject, null);

        (await PinnedCountAsync(site, subject)).Should().Be(1);
        (await PinnedCountAsync(other, subject)).Should().Be(0);
        (await PinnedCountAsync(null, subject)).Should().Be(1, "an unpinned context, as a team's managers read with, counts every site's stops");
    }

    [Fact]
    public async Task A_context_pinned_to_one_site_may_write_a_stop_for_it_and_not_for_another_site()
    {
        var site = await fixture.NewSiteAsync();
        var other = await fixture.NewSiteAsync();
        var subject = await fixture.NewSubjectAsync();

        await PinnedInsertAsync(site, site, subject);
        await ShouldViolateAsync(() => PinnedInsertAsync(site, other, subject), PostgresErrorCodes.InsufficientPrivilege);

        (await NpgsqlTestCommands.ScalarAsync<long>(
            fixture.Migrator, $"SELECT count(*) FROM team_site_blocks WHERE subject_id = '{subject}'")).Should().Be(1);
    }

    [Fact]
    public async Task Down_drops_the_stops()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'team_site_blocks'
            """)).Should().Be(0);
    }

    private async Task<long> PinnedCountAsync(Guid? site, Guid subject)
    {
        await using var conn = await PinnedAsync(site);
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM team_site_blocks WHERE subject_id = '{subject}'", conn);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }

    private async Task PinnedInsertAsync(Guid pin, Guid site, Guid subject)
    {
        await using var conn = await PinnedAsync(pin);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO team_site_blocks (id, tenant_id, subject_id)
            VALUES ('{Guid.CreateVersion7()}', '{site}', '{subject}')
            """, conn);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Guid? site)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        if (site is { } pinned)
            await conn.PinTenantAsync(pinned);
        return conn;
    }
}
