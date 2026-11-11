using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Rows stored the way the decomposer wrote them before it stopped copying curves into the
/// commands, in two tenants, rewritten by <see cref="ApsSnapshotCurvesRewriter"/> as the background
/// backfill runs it, on a real PostgreSQL.
/// </summary>
public class ApsSnapshotCurvesRewriterFixture : IAsyncLifetime
{
    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    /// <summary>Suggested and enacted from one cycle, both carrying the curves the columns hold.</summary>
    internal static readonly Guid SameCycle = Guid.Parse("c0000000-0000-7000-8000-000000000001");

    /// <summary>A suggestion uploaded without curves beside an enacted command that has them.</summary>
    internal static readonly Guid OneSided = Guid.Parse("c0000000-0000-7000-8000-000000000002");

    /// <summary>A re-sent enacted command from an older cycle, whose curves differ from the columns.</summary>
    internal static readonly Guid Resent = Guid.Parse("c0000000-0000-7000-8000-000000000003");

    /// <summary>A Loop status, with its recommended, enacted and predicted values copied out.</summary>
    internal static readonly Guid Loop = Guid.Parse("c0000000-0000-7000-8000-000000000004");

    internal Dictionary<Guid, Row> Rewritten { get; } = [];
    internal int CrossTenantChanged { get; private set; }
    internal int FirstRewriteChanged { get; private set; }
    internal int RerunChanged { get; private set; }

    internal sealed record Row(string? Suggested, string? Enacted, string? Loop);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateMigratedDatabaseAsync("aps_curves_rewriter");
        _migratorConnectionString = database.MigratorConnectionString;

        await SeedAsync();
        CrossTenantChanged = await RewriteAsync(pinnedTo: TenantA, tenant: TenantB, pageSize: 500);
        FirstRewriteChanged = await RewriteAllAsync(pageSize: 1);
        await ReadAsync(Rewritten);
        RerunChanged = await RewriteAllAsync(pageSize: 500);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DbContextOptions<NocturneDbContext> Options() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

    /// <summary>
    /// Both tenants, as the job runs them: a page of one walks every row's own page, and each page is
    /// a call of its own that resumes from the last one's <see cref="ApsSnapshotCurvesRewriter.Pass.Next"/>,
    /// as a run after a restart does.
    /// </summary>
    private async Task<int> RewriteAllAsync(int pageSize) =>
        await RewriteAsync(TenantA, TenantA, pageSize) + await RewriteAsync(TenantB, TenantB, pageSize);

    private async Task<int> RewriteAsync(Guid pinnedTo, Guid tenant, int pageSize)
    {
        var changed = 0;
        (DateTime At, Guid Id)? after = null;
        do
        {
            await using var db = new NocturneDbContext(Options()) { TenantId = pinnedTo };
            var pass = await ApsSnapshotCurvesRewriter.RewriteTenantAsync(
                db, tenant, after, maxPages: 1, pageSize, TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);
            changed += pass.Changed;
            after = pass.Next;
        }
        while (after is not null);

        return changed;
    }

    private async Task ReadAsync(Dictionary<Guid, Row> rows)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, suggested_json::text, enacted_json::text, loop_json::text FROM aps_snapshots;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows[reader.GetGuid(0)] = new Row(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }
    }

    private async Task SeedAsync()
    {
        const string curves = """{"IOB": [120, 115, 110], "ZT": [120, 118], "COB": null, "UAM": [120, 130]}""";
        const string olderCurves = """{"IOB": [125, 121, 117], "ZT": null, "COB": null, "UAM": null}""";

        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO aps_snapshots
              (id, tenant_id, timestamp, aps_system, enacted, suggested_json, enacted_json,
               predicted_iob_json, predicted_zt_json, predicted_uam_json)
            VALUES
              ('{{SameCycle}}', '{{TenantA}}', '2026-01-01T00:00:00Z', 'OpenAps', true,
               '{"bg": 120, "tick": null, "timestamp": "2026-01-01T00:00:00Z", "predBGs": {{curves}} }',
               '{"bg": 120, "rate": 0.8, "smb": null, "timestamp": "2026-01-01T00:00:00Z", "predBGs": {{curves}} }',
               '[120, 115, 110]', '[120, 118]', '[120, 130]'),
              ('{{OneSided}}', '{{TenantA}}', '2026-01-01T00:05:00Z', 'Trio', true,
               '{"bg": 91, "tick": null, "timestamp": "2026-01-01T00:05:00Z", "predBGs": null}',
               '{"bg": 91, "rate": 0.6, "timestamp": "2026-01-01T00:05:00Z", "predBGs": {{curves}} }',
               '[120, 115, 110]', '[120, 118]', '[120, 130]');

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO aps_snapshots
              (id, tenant_id, timestamp, aps_system, enacted, suggested_json, enacted_json,
               predicted_iob_json, predicted_zt_json, predicted_uam_json)
            VALUES
              ('{{Resent}}', '{{TenantB}}', '2026-01-01T00:10:00Z', 'OpenAps', true,
               '{"bg": 118, "timestamp": "2026-01-01T00:10:00Z", "predBGs": {{curves}} }',
               '{"bg": 125, "rate": 0.8, "timestamp": "2026-01-01T00:05:00Z", "predBGs": {{olderCurves}} }',
               '[120, 115, 110]', '[120, 118]', '[120, 130]');
            INSERT INTO aps_snapshots
              (id, tenant_id, timestamp, aps_system, enacted, suggested_json, enacted_json, loop_json,
               predicted_default_json)
            VALUES
              ('{{Loop}}', '{{TenantB}}', '2026-01-01T00:15:00Z', 'Loop', true,
               '{"amount": 0.4, "timestamp": null}',
               '{"rate": 2.0, "duration": 30, "received": true}',
               '{"name": "Loop", "failureReason": null,
                 "recommended": {"amount": 0.4, "timestamp": null},
                 "enacted": {"rate": 2.0, "duration": 30, "received": true},
                 "predicted": {"values": [130, 125, 120], "startDate": "2026-01-01T00:15:00Z"} }',
               '[130, 125, 120]');

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="ApsSnapshotCurvesRewriterFixture"/>
[Trait("Category", "Integration")]
public class ApsSnapshotCurvesRewriterTests(ApsSnapshotCurvesRewriterFixture fixture)
    : IClassFixture<ApsSnapshotCurvesRewriterFixture>
{
    private static IDictionary<string, JsonNode?> Json(string? text) => JsonNode.Parse(text!)!.AsObject();

    [Fact]
    public void A_context_pinned_to_one_tenant_rewrites_none_of_anothers_rows()
    {
        fixture.CrossTenantChanged.Should().Be(0);
    }

    [Fact]
    public void The_rewrite_changes_every_seeded_row_once_and_a_rerun_writes_nothing()
    {
        fixture.FirstRewriteChanged.Should().Be(4);
        fixture.RerunChanged.Should().Be(0);
    }

    [Fact]
    public void Curves_the_columns_hold_leave_both_commands_with_their_nulls()
    {
        var row = fixture.Rewritten[ApsSnapshotCurvesRewriterFixture.SameCycle];

        Json(row.Suggested).Should().NotContainKeys("predBGs", "tick");
        Json(row.Enacted).Should().NotContainKeys("predBGs", "smb");
        Json(row.Enacted)["rate"]!.GetValue<double>().Should().Be(0.8);
    }

    [Fact]
    public void A_command_uploaded_without_curves_keeps_an_explicit_null()
    {
        var row = fixture.Rewritten[ApsSnapshotCurvesRewriterFixture.OneSided];

        Json(row.Suggested).Should().ContainKey("predBGs").WhoseValue.Should().BeNull();
        Json(row.Suggested).Should().NotContainKey("tick");
        Json(row.Enacted).Should().NotContainKey("predBGs");
    }

    [Fact]
    public void An_older_cycles_curves_stay_with_their_command()
    {
        var row = fixture.Rewritten[ApsSnapshotCurvesRewriterFixture.Resent];

        Json(row.Suggested).Should().NotContainKey("predBGs");
        Json(row.Enacted)["predBGs"]!["IOB"]!.ToJsonString().Should().Be("[125,121,117]");
    }

    [Fact]
    public void Loop_copies_go_and_the_loop_status_keeps_everything_else()
    {
        var row = fixture.Rewritten[ApsSnapshotCurvesRewriterFixture.Loop];
        var loop = Json(row.Loop);

        row.Suggested.Should().BeNull();
        row.Enacted.Should().BeNull();
        loop.Should().NotContainKey("failureReason");
        Json(loop["predicted"]!.ToJsonString()).Should().NotContainKey("values");
        loop["predicted"]!["startDate"]!.GetValue<string>().Should().Be("2026-01-01T00:15:00Z");
        loop["recommended"]!["amount"]!.GetValue<double>().Should().Be(0.4);
    }
}
