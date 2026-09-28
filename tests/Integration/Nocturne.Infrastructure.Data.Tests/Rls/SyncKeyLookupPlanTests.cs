using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The keyed lookup and delete on notes, device events and BG checks read by
/// <c>(data_source, sync_identifier)</c> under the tenant and soft-delete filters, which the
/// partial unique <c>ix_&lt;table&gt;_tenant_source_sync_id</c> answers. The query the repository
/// actually sends is captured and explained as <c>nocturne_app</c>, over enough rows that a scan
/// would be the planner's choice without the index.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SyncKeyLookupPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const string DataSource = "aaps";

    [Fact]
    public Task Notes_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "notes",
            "(id, tenant_id, timestamp, text, is_announcement, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'n', false, 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await Notes(factory).FindBySyncIdentifierAsync(DataSource, key));

    [Fact]
    public Task DeviceEvents_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "device_events",
            "(id, tenant_id, timestamp, event_type, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'SiteChange', 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await DeviceEvents(factory).FindBySyncIdentifierAsync(DataSource, key));

    [Fact]
    public Task BGChecks_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "bg_checks",
            "(id, tenant_id, timestamp, glucose, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 100, 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await BGChecks(factory).FindBySyncIdentifierAsync(DataSource, key));

    private static NoteRepository Notes(TestTenantDbContextFactory factory) =>
        new(factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<NoteRepository>.Instance);

    private static DeviceEventRepository DeviceEvents(TestTenantDbContextFactory factory) =>
        new(factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);

    private static BGCheckRepository BGChecks(TestTenantDbContextFactory factory) =>
        new(factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<BGCheckRepository>.Instance);

    private async Task AssertLookupAsync(
        string table, string columns, string values, Func<TestTenantDbContextFactory, string, Task<object?>> lookup)
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, table, columns, values);

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetTenantAsync(conn, tenant);

        var capture = new LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        (await lookup(new TestTenantDbContextFactory(ctx), "sync-7777")).Should().NotBeNull();

        var plan = await ExplainAsync(conn, capture.Command!);
        Descendants(plan, "Node Type").Should().NotContain("Seq Scan", plan.ToString());
        Descendants(plan, "Index Name").Should().Equal(
            [$"ix_{table}_tenant_source_sync_id"], $"the sync key has a partial unique index to probe; plan: {plan}");
    }

    private async Task SeedAsync(Guid tenant, string table, string columns, string values)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'sync-key-plan-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"sync-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        await SetTenantAsync(conn, tenant);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {table} {columns} SELECT {values} FROM generate_series(1, {Rows}) g";
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

    private static IEnumerable<string> Descendants(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name == property && p.Value.ValueKind == JsonValueKind.String)
                    yield return p.Value.GetString()!;
                foreach (var d in Descendants(p.Value, property))
                    yield return d;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var d in Descendants(item, property))
                yield return d;
        }
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
