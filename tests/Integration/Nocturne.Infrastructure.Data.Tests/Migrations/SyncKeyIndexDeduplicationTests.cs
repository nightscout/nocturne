using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddSyncKeyIndexesToBgChecksNotesAndDeviceEvents</c> over a database that already holds
/// one live sync key twice in each table, as an instance upgrading from before the index can.
/// </summary>
/// <remarks>
/// The loser cleanup is a <c>DO</c> block that loops zero tenants on an empty database, so only a
/// seeded run shows it clears the way for the unique build. One run, one seed.
/// </remarks>
public class SyncKeyIndexDeduplicationFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20260927185944_AddEntryFoodProfileHistoryIndexes";

    internal static readonly Guid Tenant = Guid.Parse("44444444-4444-7444-8444-444444444444");

    internal static readonly string[] Tables = ["bg_checks", "notes", "device_events"];

    private string _migratorConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("sync_key_dedup");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<NpgsqlConnection> OpenPinnedAsync()
    {
        var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var pin = conn.CreateCommand();
        pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{Tenant}', false);";
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    internal async Task<List<(string Label, bool Deleted)>> RowsAsync(string table)
    {
        await using var conn = await OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT label, deleted_at IS NOT NULL FROM (SELECT additional_properties->>'label' AS label, deleted_at FROM {table}) r ORDER BY label;";
        var rows = new List<(string, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetBoolean(1)));
        return rows;
    }

    internal async Task<(bool Unique, bool Valid)> IndexAsync(string table)
    {
        await using var conn = await OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT indisunique, indisvalid FROM pg_index WHERE indexrelid = to_regclass('public.ix_{table}_tenant_source_sync_id');";
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"ix_{table}_tenant_source_sync_id should exist");
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task SeedAsync()
    {
        await using var conn = await OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{{Tenant}}', 'sync-dedup', 'SyncDedup', true, now(), now());
            {{string.Join("\n", Tables.Select(Seed))}}
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Per table: a live pair under one key (older and newer insert), a pair with no data source,
    /// and a key held once live and once already deleted.
    /// </summary>
    private static string Seed(string table)
    {
        var (columns, values) = table switch
        {
            "bg_checks" => ("glucose", "100"),
            "notes" => ("text, is_announcement", "'n', false"),
            _ => ("event_type", "'SiteChange'"),
        };

        string Row(string label, string? dataSource, string sync, string createdAt, string deletedAt) =>
            $$"""
            INSERT INTO {{table}} (id, tenant_id, timestamp, {{columns}}, data_source, sync_identifier, additional_properties, sys_created_at, sys_updated_at, deleted_at)
            VALUES (gen_random_uuid(), '{{Tenant}}', now(), {{values}}, {{(dataSource is null ? "NULL" : $"'{dataSource}'")}}, '{{sync}}', '{"label":"{{label}}"}'::jsonb, {{createdAt}}, now(), {{deletedAt}});
            """;

        return string.Join("\n",
            Row("dup-older", "aaps", "dup", "now() - interval '1 hour'", "NULL"),
            Row("dup-newer", "aaps", "dup", "now()", "NULL"),
            Row("nosource-a", null, "nosource", "now() - interval '1 hour'", "NULL"),
            Row("nosource-b", null, "nosource", "now()", "NULL"),
            Row("held-deleted", "aaps", "held", "now()", "now()"),
            Row("held-live", "aaps", "held", "now() - interval '1 hour'", "NULL"));
    }
}

/// <inheritdoc cref="SyncKeyIndexDeduplicationFixture"/>
[Trait("Category", "Integration")]
public class SyncKeyIndexDeduplicationTests(SyncKeyIndexDeduplicationFixture fixture)
    : IClassFixture<SyncKeyIndexDeduplicationFixture>
{
    public static TheoryData<string> Tables => [.. SyncKeyIndexDeduplicationFixture.Tables];

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task The_newest_insert_of_a_duplicated_key_survives_and_the_rest_are_soft_deleted(string table)
    {
        var rows = await fixture.RowsAsync(table);

        rows.Should().Contain(("dup-newer", false)).And.Contain(("dup-older", true));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Rows_the_index_does_not_constrain_are_left_alone(string table)
    {
        var rows = await fixture.RowsAsync(table);

        rows.Should().Contain(("nosource-a", false)).And.Contain(("nosource-b", false),
            "a null data source never collides in the index");
        rows.Should().Contain(("held-live", false)).And.Contain(("held-deleted", true),
            "an already-deleted row is outside the index, so the live row is the only holder");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task The_index_is_built_unique_and_valid(string table)
    {
        var (unique, valid) = await fixture.IndexAsync(table);

        unique.Should().BeTrue();
        valid.Should().BeTrue("a concurrent build that failed would leave an invalid index behind");
    }
}
