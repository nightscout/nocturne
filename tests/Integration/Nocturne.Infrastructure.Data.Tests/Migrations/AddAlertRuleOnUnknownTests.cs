using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddAlertRuleOnUnknown</c> on a real PostgreSQL as the migrator: a rule stored before it
/// reads <c>hold</c>, as does a row any later writer inserts without the column
/// (docs/alerts/engine-semantics.md §6.4); Down removes the column.
/// </summary>
public class AddAlertRuleOnUnknownFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261121042204_AddUnevaluableConditionVersionToAlertExcursions";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111113595");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222223595");

    private string _migrator = string.Empty;

    internal List<string> WordsOfStoredRules { get; private set; } = [];
    internal List<string> WordsOfRulesInsertedWithoutIt { get; private set; } = [];
    internal long ColumnsAfterDown { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "alert_rule_on_unknown");
        _migrator = database.MigratorConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB })
            await SeedAsync(tenant, "stored");

        await MigrateToAsync(null);
        WordsOfStoredRules = await PerTenantAsync("SELECT on_unknown FROM alert_rules WHERE name = 'stored'");

        foreach (var tenant in new[] { TenantA, TenantB })
            await InsertRuleAsync(tenant, "inserted");
        WordsOfRulesInsertedWithoutIt = await PerTenantAsync("SELECT on_unknown FROM alert_rules WHERE name = 'inserted'");

        await MigrateToAsync(PriorMigration);
        ColumnsAfterDown = await NpgsqlTestCommands.ScalarAsync<long>(_migrator,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'alert_rules' AND column_name = 'on_unknown' AND table_schema = 'public'");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(Guid tenant, string name)
    {
        await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'on-unknown-{tenant:N}', 'on-unknown', true, now(), now());
            """);
        await InsertRuleAsync(tenant, name);
    }

    private Task InsertRuleAsync(Guid tenant, string name) => NpgsqlTestCommands.ExecuteAsync(_migrator, $$"""
        SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
        INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, sort_order, severity)
        VALUES (gen_random_uuid(), '{{tenant}}', '{{name}}', 'threshold', '{"direction":"below","value":70}', 0, 'warning');
        """);

    private DbContextOptions<NocturneDbContext> Options() =>
        TestContexts.MigrationsOptions(_migrator);

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
}

[Trait("Category", "Integration")]
public class AddAlertRuleOnUnknownTests(AddAlertRuleOnUnknownFixture fixture)
    : IClassFixture<AddAlertRuleOnUnknownFixture>
{
    [Fact]
    public void A_rule_stored_before_the_migration_reads_hold() =>
        fixture.WordsOfStoredRules.Should().Equal("hold", "hold");

    [Fact]
    public void A_row_inserted_without_the_column_reads_hold() =>
        fixture.WordsOfRulesInsertedWithoutIt.Should().Equal("hold", "hold");

    [Fact]
    public void Down_drops_the_column() => fixture.ColumnsAfterDown.Should().Be(0);
}
