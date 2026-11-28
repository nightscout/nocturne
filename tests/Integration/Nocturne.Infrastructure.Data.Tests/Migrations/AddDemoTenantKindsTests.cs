using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddDemoTenantKinds</c> on a real PostgreSQL as the migrator over a database holding a demo tenant, then
/// exercises the kind's backfill and check, and the clinic team's key. A second database runs it up and back down.
/// </summary>
public class AddDemoTenantKindsFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261128011340_RetireScopeClassBackfillJobV1";

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingDemo { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_demo_tenant_kinds");
        Migrator = database.MigratorConnectionString;
        await NpgsqlTestCommands.SeedTenantAsync(Migrator, ExistingDemo, $"demo-{ExistingDemo:N}", "Demo");
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            UPDATE tenants SET is_demo = true WHERE id = '{ExistingDemo}';
            INSERT INTO tenant_demo_config (tenant_id, access_mode, backfill_days, interval_minutes, reset_interval_minutes)
            VALUES ('{ExistingDemo}', 'open', 90, 5, 0);
            """);

        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_demo_tenant_kinds_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<Guid> NewDemoTenantAsync(string kind)
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.SeedTenantAsync(Migrator, tenant, $"kind-{tenant:N}", "Demo person");
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenant_demo_config (tenant_id, access_mode, backfill_days, interval_minutes, reset_interval_minutes, kind)
            VALUES ('{tenant}', 'open', 30, 5, 0, '{kind}');
            """);
        return tenant;
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddDemoTenantKindsTests(AddDemoTenantKindsFixture fixture) : IClassFixture<AddDemoTenantKindsFixture>
{
    [Fact]
    public async Task The_demo_tenant_there_was_is_the_host_with_no_clinic()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM tenant_demo_config
             WHERE tenant_id = '{fixture.ExistingDemo}' AND kind = 'host' AND clinic_team_id IS NULL
            """)).Should().Be(1);
    }

    [Fact]
    public async Task A_kind_is_a_host_or_a_person()
    {
        await fixture.NewDemoTenantAsync("person");

        var other = () => fixture.NewDemoTenantAsync("clinic");

        (await other.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Deleting_the_clinic_team_leaves_the_host_without_one()
    {
        var host = await fixture.NewDemoTenantAsync("host");
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"""
            INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');
            UPDATE tenant_demo_config SET clinic_team_id = '{team}' WHERE tenant_id = '{host}';
            """);

        var unknown = () => NpgsqlTestCommands.ExecuteAsync(fixture.Migrator,
            $"UPDATE tenant_demo_config SET clinic_team_id = '{Guid.CreateVersion7()}' WHERE tenant_id = '{host}'");
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM teams WHERE id = '{team}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"""
            SELECT count(*) FROM tenant_demo_config WHERE tenant_id = '{host}' AND clinic_team_id IS NULL
            """)).Should().Be(1);
    }

    [Fact]
    public async Task The_clinic_team_index_is_built_and_valid()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
             WHERE c.relname = 'ix_tenant_demo_config_clinic_team_id' AND i.indisvalid
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Down_drops_the_kind_the_clinic_team_and_its_index()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_name = 'tenant_demo_config' AND column_name IN ('kind', 'clinic_team_id')
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_tenant_demo_config_clinic_team_id'")).Should().Be(0);
    }
}
