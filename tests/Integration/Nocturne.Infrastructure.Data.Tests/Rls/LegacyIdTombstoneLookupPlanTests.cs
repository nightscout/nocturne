using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// <see cref="SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync{TEntity}(NocturneDbContext, HashSet{string}, CancellationToken)"/>
/// reads user tombstones as well as live rows, which the partial unique <c>(tenant_id, legacy_id)</c>
/// index leaves out. Without <c>ix_&lt;table&gt;_tenant_legacy_id_user_deleted</c> the planner walks
/// every row the tenant owns (#1270). The query EF actually sends is captured and explained as
/// <c>nocturne_app</c>, so a change to either the LINQ or the index family that loses the probe fails here.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class LegacyIdTombstoneLookupPlanTests(RlsCompletenessFixture fx)
{
    private const int LiveRows = 20_000;
    private const int SweptRows = 1_000;
    private const int UserDeletedRows = 1_000;

    [Fact]
    public Task SensorGlucose_LookupProbesBothLegacyIdIndexes() =>
        AssertLookupAsync<SensorGlucoseEntity>(
            "sensor_glucose",
            "(id, tenant_id, timestamp, mgdl, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 100, {0}, {1}, {2}, now(), now()");

    [Fact]
    public Task TempBasals_LookupProbesBothLegacyIdIndexes() =>
        AssertLookupAsync<TempBasalEntity>(
            "temp_basals",
            "(id, tenant_id, start_timestamp, rate, origin, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 1.0, 'Algorithm', {0}, {1}, {2}, now(), now()");

    private async Task AssertLookupAsync<TEntity>(string table, string columns, string valuesTemplate)
        where TEntity : class, IV4Entity
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, table, columns, valuesTemplate);

        var lookedUp = Enumerable.Range(1, 50).Select(i => $"live-{i * 300}")
            .Concat(Enumerable.Range(1, 50).Select(i => $"swept-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"user-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"absent-{i}"))
            .ToHashSet(StringComparer.Ordinal);

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetTenantAsync(conn, tenant);

        var capture = new LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var blocks = await ctx.GetBlockingLegacyIdsAsync<TEntity>(lookedUp);

        blocks.Held.Should().BeEquivalentTo(
            lookedUp.Where(id => id.StartsWith("live-") || id.StartsWith("user-")),
            "live rows and user tombstones block re-creation; system sweeps and unknown ids do not");
        blocks.DeletedByUser.Should().BeEquivalentTo(lookedUp.Where(id => id.StartsWith("user-")));

        var plan = await ExplainAsync(conn, capture.Command!);
        var indexes = plan.Descendants("Index Name").ToList();
        var nodes = plan.Descendants("Node Type").ToList();

        nodes.Should().NotContain("Seq Scan", plan.ToString());
        indexes.Should().BeEquivalentTo(
            [$"ix_{table}_tenant_legacy_id", $"ix_{table}_tenant_legacy_id_user_deleted"],
            $"each arm of the live-or-user-tombstone OR has a partial index to probe; plan: {plan}");
    }

    private async Task SeedAsync(Guid tenant, string table, string columns, string valuesTemplate)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'tombstone-plan-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"tomb-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        await SetTenantAsync(conn, tenant);

        (string Prefix, int Count, string DeletedAt, string ByUser)[] batches =
        [
            ("live", LiveRows, "NULL", "false"),
            ("swept", SweptRows, "now()", "false"),
            ("user", UserDeletedRows, "now()", "true"),
        ];

        foreach (var (prefix, count, deletedAt, byUser) in batches)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {table} {columns}
                SELECT {string.Format(valuesTemplate, $"'{prefix}-' || g", deletedAt, byUser)}
                FROM generate_series(1, {count}) g
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = $"ANALYZE {table}";
        await analyze.ExecuteNonQueryAsync();
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
        cmd.Parameters.AddWithValue("tid", tenant.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> ExplainAsync(NpgsqlConnection conn, NpgsqlCommand captured)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + captured.CommandText;
        foreach (NpgsqlParameter p in captured.Parameters)
            cmd.Parameters.Add(p.Clone());

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed class LastReaderCapture : DbCommandInterceptor
    {
        public NpgsqlCommand? Command { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var copy = new NpgsqlCommand(command.CommandText);
            foreach (NpgsqlParameter p in command.Parameters)
                copy.Parameters.Add(p.Clone());
            Command = copy;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

internal static class PlanJson
{
    public static IEnumerable<string> Descendants(this JsonElement element, string property)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name == property && p.Value.ValueKind == JsonValueKind.String)
                        yield return p.Value.GetString()!;
                    foreach (var d in p.Value.Descendants(property))
                        yield return d;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var d in item.Descendants(property))
                    yield return d;
                break;
        }
    }
}
