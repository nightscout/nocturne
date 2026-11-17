using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddSecondaryStreamFlagToSensorGlucose</c> on a real PostgreSQL as the migrator: the
/// column arrives false on existing readings, every tenant's recompute row is due, a boot killed
/// before the history row re-runs it cleanly, and Down removes both.
/// </summary>
public class AddSecondaryStreamFlagToSensorGlucoseFixture : IAsyncLifetime
{
    private const string Migration = "20261102093846_AddSecondaryStreamFlagToSensorGlucose";
    private const string PriorMigration = "20261101213815_IndexVisibleRowsOfDeduplicatedTables";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111112");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222223");

    private string _migrator = string.Empty;

    internal List<string> FlagsAfterUp { get; private set; } = [];
    internal List<string> RowsAfterUp { get; private set; } = [];
    internal List<string> RowsAfterRestart { get; private set; } = [];
    internal long ColumnsAfterDown { get; private set; }
    internal long RowsAfterDown { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "secondary_stream_flag");
        _migrator = database.MigratorConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB })
            await SeedAsync(tenant);

        await MigrateToAsync(null);
        FlagsAfterUp = await PerTenantAsync("SELECT is_secondary_stream::text FROM sensor_glucose");
        RowsAfterUp = await LedgerRowsAsync();

        await ExecuteAsync($"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Migration}'");
        await MigrateToAsync(null);
        RowsAfterRestart = await LedgerRowsAsync();

        await MigrateToAsync(PriorMigration);
        ColumnsAfterDown = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns WHERE column_name = 'is_secondary_stream' AND table_schema = 'public'");
        RowsAfterDown = await ScalarAsync(
            $"SELECT count(*) FROM job_ledger WHERE job = '{AddSecondaryStreamFlagToSensorGlucose.JobName}'");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(Guid tenant)
    {
        await ExecuteAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'stream-{tenant:N}', 'secondary-stream', true, now(), now());
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO sensor_glucose (id, tenant_id, timestamp, mgdl, data_source, device, sys_created_at, sys_updated_at)
            VALUES (gen_random_uuid(), '{tenant}', now(), 110, 'xdrip', 'phone', now(), now()),
                   (gen_random_uuid(), '{tenant}', now(), 120, 'dexcom-connector', 'share', now(), now());
            """);
    }

    /// <summary>Each recompute row as tenant and whether it is due by now.</summary>
    private async Task<List<string>> LedgerRowsAsync()
    {
        var rows = new List<string>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT tenant_id::text || ':' || (next_due_at <= now())::text FROM job_ledger
            WHERE job = '{AddSecondaryStreamFlagToSensorGlucose.JobName}' ORDER BY tenant_id
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private DbContextOptions<NocturneDbContext> Options() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

    private async Task MigrateToAsync(string? target)
    {
        await using var context = new NocturneDbContext(Options());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<List<string>> PerTenantAsync(string sql)
    {
        var rows = new List<string>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var transaction = await conn.BeginTransactionAsync();
            await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', true)", conn))
                await pin.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public class AddSecondaryStreamFlagToSensorGlucoseTests(AddSecondaryStreamFlagToSensorGlucoseFixture fixture)
    : IClassFixture<AddSecondaryStreamFlagToSensorGlucoseFixture>
{
    private static readonly string[] DueForEveryTenant =
    [
        $"{AddSecondaryStreamFlagToSensorGlucoseFixture.TenantA}:true",
        $"{AddSecondaryStreamFlagToSensorGlucoseFixture.TenantB}:true",
    ];

    [Fact]
    public void Up_AddsTheFlagFalse_AndMakesEveryTenantsRecomputeDue()
    {
        fixture.FlagsAfterUp.Should().HaveCount(4).And.OnlyContain(flag => flag == "false");
        fixture.RowsAfterUp.Should().BeEquivalentTo(DueForEveryTenant);
    }

    [Fact]
    public void ABootKilledBeforeTheHistoryRow_ReRunsItCleanly() =>
        fixture.RowsAfterRestart.Should().BeEquivalentTo(DueForEveryTenant);

    [Fact]
    public void Down_DropsTheColumnAndTheLedgerRows()
    {
        fixture.ColumnsAfterDown.Should().Be(0);
        fixture.RowsAfterDown.Should().Be(0);
    }
}
