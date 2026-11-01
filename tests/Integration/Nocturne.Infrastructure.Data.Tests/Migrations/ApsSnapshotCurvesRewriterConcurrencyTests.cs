using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>A migrated database holding one oref snapshot whose command still carries its curves.</summary>
public class ApsSnapshotCurvesRewriterConcurrencyFixture : IAsyncLifetime
{
    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid Snapshot = Guid.Parse("c0000000-0000-7000-8000-000000000001");

    internal const string Curves = """{"IOB": [120, 115, 110], "ZT": [120, 118], "UAM": [120, 130]}""";

    internal string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("aps_curves_concurrency");
        ConnectionString = database.MigratorConnectionString;

        await using (var context = new NocturneDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var conn = await OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{{Tenant}}', 'tenant-a', 'Tenant A', true, now(), now());
            INSERT INTO aps_snapshots
              (id, tenant_id, timestamp, aps_system, enacted, suggested_json,
               predicted_iob_json, predicted_zt_json, predicted_uam_json)
            VALUES
              ('{{Snapshot}}', '{{Tenant}}', '2026-01-01T00:00:00Z', 'OpenAps', false,
               '{"bg": 120, "tick": null, "predBGs": {{Curves}} }',
               '[120, 115, 110]', '[120, 118]', '[120, 130]');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<NpgsqlConnection> OpenPinnedAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var pin = conn.CreateCommand();
        pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{Tenant}', false);";
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    internal DbContextOptions<NocturneDbContext> Options() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
}

/// <summary>
/// An upload that changes a snapshot while <see cref="ApsSnapshotCurvesRewriter"/> rewrites it is
/// kept: the rewrite waits on the upload's lock and rewrites the upload's version, on a real
/// PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class ApsSnapshotCurvesRewriterConcurrencyTests(ApsSnapshotCurvesRewriterConcurrencyFixture fixture)
    : IClassFixture<ApsSnapshotCurvesRewriterConcurrencyFixture>
{
    private static readonly Guid Tenant = ApsSnapshotCurvesRewriterConcurrencyFixture.Tenant;
    private static readonly Guid Snapshot = ApsSnapshotCurvesRewriterConcurrencyFixture.Snapshot;
    private const string Curves = ApsSnapshotCurvesRewriterConcurrencyFixture.Curves;

    [Fact]
    public async Task A_snapshot_an_upload_changes_mid_rewrite_is_rewritten_from_the_uploads_version()
    {
        await using var upload = await fixture.OpenPinnedAsync();
        await using var transaction = await upload.BeginTransactionAsync();
        await using (var change = upload.CreateCommand())
        {
            change.Transaction = transaction;
            change.CommandText = $$"""
                UPDATE aps_snapshots
                SET suggested_json = '{"bg": 200, "tick": null, "predBGs": {{Curves}} }'
                WHERE id = '{{Snapshot}}';
                """;
            await change.ExecuteNonQueryAsync();
        }

        var rewrite = Task.Run(async () =>
        {
            await using var db = new NocturneDbContext(fixture.Options()) { TenantId = Tenant };
            return (await ApsSnapshotCurvesRewriter.RewriteTenantAsync(
                db, Tenant, after: null, int.MaxValue, 500, TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None)).Changed;
        });

        await WaitUntilBlockedOnALockAsync();
        rewrite.IsCompleted.Should().BeFalse("the rewrite waits on the upload's row lock");
        await transaction.CommitAsync();

        (await rewrite).Should().Be(1);
        IDictionary<string, JsonNode?> suggested = JsonNode.Parse(await ReadSuggestedAsync())!.AsObject();
        suggested["bg"]!.GetValue<int>().Should().Be(200);
        suggested.Should().NotContainKeys("predBGs", "tick");
    }

    private async Task WaitUntilBlockedOnALockAsync()
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var cmd = observer.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%WITH page AS%';
                """;
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The rewrite never waited on the upload's lock.");
    }

    private async Task<string> ReadSuggestedAsync()
    {
        await using var conn = await fixture.OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT suggested_json::text FROM aps_snapshots WHERE id = '{Snapshot}';";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
