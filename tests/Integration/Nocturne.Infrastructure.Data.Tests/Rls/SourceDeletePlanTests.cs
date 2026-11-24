using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A by-source soft delete on tables the planner has no statistics for, as they are after a first large
/// import or a restore: each chunk's update reads the table once and does not join back to the selection.
/// </summary>
/// <remarks>
/// It pins the statement's shape, not its cost at scale: a scan of the tenant's rows filtered by id
/// satisfies it, and it runs two chunks on a connection that does not auto-prepare, so it never sees a
/// generic plan.
/// </remarks>
[Trait("Category", "Integration")]
public class SourceDeletePlanTests(SourceDeletePlanTests.NeverAnalysedDatabase db)
    : IClassFixture<SourceDeletePlanTests.NeverAnalysedDatabase>
{
    private const int Groups = 2 * NocturneDbContext.SystemTimestampGroupSize;

    [Fact]
    public async Task DeletingASource_StampsEachChunkByIdInOneScan()
    {
        var tenant = Guid.NewGuid();
        await db.SeedAsync(tenant, Groups);

        await using var conn = await db.OpenAppConnectionAsync(tenant);
        var recorder = new UpdateRecorder();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = tenant };

        var clock = Stopwatch.StartNew();
        var deleted = await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.Selected(RecordSelector.ForSource("source-a")),
            SystemAuditContext.ForService("test"), "data_source=source-a");
        clock.Stop();

        deleted.Should().Be(Groups);
        recorder.Stamps.Should().HaveCount(2, "one update per chunk of ids");
        var plan = await PlanTestSupport.ExplainAsync(conn, recorder.Stamps[0]);
        plan.Descendants("Relation Name").Should().Equal(
            ["sensor_glucose", "sensor_glucose"], "the update reads its table once: " + plan);
        plan.Descendants("Node Type").Should().NotContain(
            ["Nested Loop", "Hash Join", "Merge Join", "Append", "Seq Scan"], plan.ToString());
        plan.Descendants("Index Cond").Concat(plan.Descendants("Filter")).Should().Contain(
            c => c.Contains("(id = ANY ("), "the scan keeps only the chunk's ids: " + plan);
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10), "the whole delete takes about a second; the statement timeout is 30 s");
    }

    /// <summary>The updates that stamp <c>sensor_glucose</c> rows deleted, with their parameters.</summary>
    private sealed class UpdateRecorder : DbCommandInterceptor
    {
        public List<NpgsqlCommand> Stamps { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE sensor_glucose", StringComparison.Ordinal)
                && command.CommandText.Contains("deleted_at = ", StringComparison.Ordinal))
            {
                var copy = new NpgsqlCommand(command.CommandText);
                foreach (NpgsqlParameter p in command.Parameters)
                    copy.Parameters.Add(p.Clone());
                Stamps.Add(copy);
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// A database of its own whose glucose and link tables are never analysed: autovacuum is off on
    /// them, and nothing else in the run writes to it.
    /// </summary>
    public sealed class NeverAnalysedDatabase : IAsyncLifetime
    {
        private TestDatabase _database = null!;

        public async Task InitializeAsync()
        {
            _database = await SharedPostgres.CreateMigratedDatabaseAsync("source_delete_plan");
            await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE sensor_glucose SET (autovacuum_enabled = false);
                ALTER TABLE linked_records SET (autovacuum_enabled = false);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// Sends the runtime role's connections to this database, from the next one opened, the plan
        /// of every statement they run as a notice, prepared statements' generic plans included.
        /// </summary>
        public async Task SendPlansAsNoticesAsync()
        {
            await using var conn = new NpgsqlConnection(_database.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                ALTER ROLE nocturne_app IN DATABASE "{_database.Name}" SET session_preload_libraries = 'auto_explain';
                ALTER ROLE nocturne_app IN DATABASE "{_database.Name}" SET auto_explain.log_min_duration = 0;
                ALTER ROLE nocturne_app IN DATABASE "{_database.Name}" SET auto_explain.log_level = 'notice';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>The runtime role's connection string, for a pool built as the API builds its own.</summary>
        public string AppConnectionString => _database.AppConnectionString;

        public async Task<NpgsqlConnection> OpenAppConnectionAsync(Guid tenant)
        {
            var conn = new NpgsqlConnection(_database.AppConnectionString);
            await conn.OpenAsync();
            await PlanTestSupport.SetTenantAsync(conn, tenant);
            return conn;
        }

        /// <summary>
        /// <paramref name="groups"/> duplicate groups for <paramref name="tenant"/>, each a reading from
        /// <c>source-a</c>, its group's primary, and one from <c>source-b</c>.
        /// </summary>
        public async Task SeedAsync(Guid tenant, int groups)
        {
            await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'source-delete-plan-test', true, now(), now());
                SELECT set_config('app.current_tenant_id', @tid::text, false);
                WITH g AS MATERIALIZED (
                    SELECT gen_random_uuid() AS canonical, gen_random_uuid() AS a, gen_random_uuid() AS b,
                           date_trunc('minute', now()) - make_interval(mins => i) AS ts
                    FROM generate_series(1, @groups) i
                ), glucose AS (
                    INSERT INTO sensor_glucose (id, tenant_id, timestamp, mgdl, data_source, deleted_by_user, sys_created_at, sys_updated_at)
                    SELECT a, @tid, ts, 100, 'source-a', false, now(), now() FROM g
                    UNION ALL
                    SELECT b, @tid, ts, 101, 'source-b', false, now(), now() FROM g
                )
                INSERT INTO linked_records (id, tenant_id, canonical_id, record_type, record_id, data_source, is_primary, source_timestamp, sys_created_at)
                SELECT gen_random_uuid(), @tid, canonical, @type, a, 'source-a', true, (extract(epoch FROM ts) * 1000)::bigint, now() FROM g
                UNION ALL
                SELECT gen_random_uuid(), @tid, canonical, @type, b, 'source-b', false, (extract(epoch FROM ts) * 1000)::bigint, now() FROM g;
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"srcplan-{tenant:N}");
            cmd.Parameters.AddWithValue("groups", groups);
            cmd.Parameters.AddWithValue("type", RecordTypeKeys.Key(RecordType.SensorGlucose));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
