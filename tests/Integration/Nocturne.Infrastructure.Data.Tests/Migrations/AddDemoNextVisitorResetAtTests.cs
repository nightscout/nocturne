using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddDemoNextVisitorResetAt</c> on a real PostgreSQL as the migrator over a database holding a demo host, then
/// reads and writes the new column. A second database runs it up and back down.
/// </summary>
public class AddDemoNextVisitorResetAtFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261128042040_AddDemoTenantKinds";

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingDemo { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_demo_next_visitor_reset_at");
        Migrator = database.MigratorConnectionString;
        await NpgsqlTestCommands.SeedTenantAsync(Migrator, ExistingDemo, $"demo-{ExistingDemo:N}", "Demo");
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            UPDATE tenants SET is_demo = true WHERE id = '{ExistingDemo}';
            INSERT INTO tenant_demo_config (tenant_id, access_mode, backfill_days, interval_minutes, reset_interval_minutes, kind)
            VALUES ('{ExistingDemo}', 'open', 90, 5, 1440, 'host');
            """);

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_demo_next_visitor_reset_at_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddDemoNextVisitorResetAtTests(AddDemoNextVisitorResetAtFixture fixture) : IClassFixture<AddDemoNextVisitorResetAtFixture>
{
    [Fact]
    public async Task The_demo_host_there_was_keeps_its_config_with_no_visitor_reset_recorded()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM tenant_demo_config
             WHERE tenant_id = '{fixture.ExistingDemo}' AND reset_interval_minutes = 1440 AND next_visitor_reset_at IS NULL
            """)).Should().Be(1);
    }

    [Fact]
    public async Task A_visitor_reset_time_is_stored_as_given()
    {
        var host = Guid.CreateVersion7();
        await NpgsqlTestCommands.SeedTenantAsync(fixture.Migrator, host, $"host-{host:N}", "Demo host");
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"""
            INSERT INTO tenant_demo_config (tenant_id, access_mode, backfill_days, interval_minutes, reset_interval_minutes, kind, next_visitor_reset_at)
            VALUES ('{host}', 'open', 30, 5, 0, 'host', '2026-11-28T08:00:00Z');
            """);

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM tenant_demo_config
             WHERE tenant_id = '{host}' AND next_visitor_reset_at = timestamptz '2026-11-28T08:00:00Z'
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Down_drops_the_column()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_name = 'tenant_demo_config' AND column_name = 'next_visitor_reset_at'
            """)).Should().Be(0);
    }
}
