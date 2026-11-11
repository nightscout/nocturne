using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>IndexVisibleRowsOfDeduplicatedTables</c> on a real PostgreSQL as the migrator: Up leaves
/// only the valid partial list indexes, a boot killed after some builds and before the drops re-runs
/// into the same state, and Down then Up lands there again.
/// </summary>
public class IndexVisibleRowsOfDeduplicatedTablesFixture : IAsyncLifetime
{
    private const string Migration = "20261101213815_IndexVisibleRowsOfDeduplicatedTables";
    private const string PriorMigration = "20261101194922_AddDuplicateFlagToLinkedRecordTypes";

    private TestDatabase _database = null!;

    internal Dictionary<string, string> AfterUp { get; private set; } = [];
    internal Dictionary<string, string> AfterRestart { get; private set; } = [];
    internal Dictionary<string, string> AfterDown { get; private set; } = [];
    internal Dictionary<string, string> AfterSecondUp { get; private set; } = [];

    public async Task InitializeAsync()
    {
        _database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "visible_list_indexes");

        await MigrateToAsync(null);
        AfterUp = await ListIndexesAsync();

        await ExecuteAsync(_database.SuperuserConnectionString, """
            UPDATE pg_index SET indisvalid = false
            WHERE indexrelid = 'public.ix_notes_tenant_visible_timestamp_id'::regclass
            """);
        await ExecuteAsync(_database.MigratorConnectionString, """
            CREATE INDEX ix_boluses_tenant_timestamp_id ON boluses (tenant_id, "timestamp" DESC, id DESC)
            WHERE deleted_at IS NULL
            """);
        await ExecuteAsync(_database.MigratorConnectionString,
            $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Migration}'");
        await MigrateToAsync(null);
        AfterRestart = await ListIndexesAsync();

        await MigrateToAsync(PriorMigration);
        AfterDown = await ListIndexesAsync();
        await MigrateToAsync(null);
        AfterSecondUp = await ListIndexesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        await using var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_database.MigratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>Every list index the migration touches, by name, as its definition and validity.</summary>
    private async Task<Dictionary<string, string>> ListIndexesAsync()
    {
        var indexes = new Dictionary<string, string>();
        await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT c.relname, pg_get_indexdef(i.indexrelid) || CASE WHEN i.indisvalid THEN '' ELSE ' INVALID' END
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname ~ '_tenant_(visible_)?(timestamp|start)_id$'
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes[reader.GetString(0)] = reader.GetString(1);
        return indexes;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public class IndexVisibleRowsOfDeduplicatedTablesTests(IndexVisibleRowsOfDeduplicatedTablesFixture fixture)
    : IClassFixture<IndexVisibleRowsOfDeduplicatedTablesFixture>
{
    private static readonly string[] Deduplicated =
        ["sensor_glucose", "boluses", "carb_intakes", "bg_checks", "notes", "device_events", "bolus_calculations", "temp_basals"];

    private static readonly string[] Untouched =
        ["meter_glucose", "calibrations", "aps_snapshots", "pump_snapshots", "uploader_snapshots"];

    private static Dictionary<string, string> Expected()
    {
        var expected = Deduplicated.ToDictionary(
            table => $"ix_{table}_tenant_visible_timestamp_id",
            table => $"CREATE INDEX ix_{table}_tenant_visible_timestamp_id ON public.{table} USING btree "
                + $"(tenant_id, {(table == "temp_basals" ? "start_timestamp" : "\"timestamp\"")} DESC, id DESC) "
                + "WHERE ((deleted_at IS NULL) AND (NOT is_duplicate))");
        expected["ix_state_spans_tenant_visible_start_id"] =
            "CREATE INDEX ix_state_spans_tenant_visible_start_id ON public.state_spans USING btree "
            + "(tenant_id, start_timestamp DESC, id DESC) WHERE ((deleted_at IS NULL) AND (NOT is_duplicate))";
        foreach (var table in Untouched)
            expected[$"ix_{table}_tenant_timestamp_id"] =
                $"CREATE INDEX ix_{table}_tenant_timestamp_id ON public.{table} USING btree "
                + "(tenant_id, \"timestamp\" DESC, id DESC) WHERE (deleted_at IS NULL)";
        return expected;
    }

    [Fact]
    public void Up_LeavesOnlyTheValidPartialIndexesOnTheDeduplicatedTables() =>
        fixture.AfterUp.Should().BeEquivalentTo(Expected());

    [Fact]
    public void ABootKilledAfterSomeBuildsAndBeforeTheDrops_ReRunsIntoTheSameState() =>
        fixture.AfterRestart.Should().BeEquivalentTo(Expected());

    [Fact]
    public void Down_RestoresTheLiveRowIndexes_AndUpReplacesThemAgain()
    {
        fixture.AfterDown.Keys.Should().BeEquivalentTo(
            Deduplicated.Except(["bolus_calculations", "temp_basals"]).Concat(Untouched)
                .Select(table => $"ix_{table}_tenant_timestamp_id")
                .Append("ix_state_spans_tenant_start_id"));
        fixture.AfterDown.Values.Should().OnlyContain(definition => definition.EndsWith("WHERE (deleted_at IS NULL)"));
        fixture.AfterSecondUp.Should().BeEquivalentTo(Expected());
    }
}
