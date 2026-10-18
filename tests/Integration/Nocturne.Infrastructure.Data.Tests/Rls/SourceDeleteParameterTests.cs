using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A by-source soft delete filters by lists as long as the source's history: the deleted ids, the
/// groups they led, their links. Npgsql binds each as one array parameter (<c>= ANY (@p)</c>); a
/// parameter per id would hit PostgreSQL's 65535-parameter limit on a large source and re-plan on
/// every length.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SourceDeleteParameterTests(RlsCompletenessFixture fx)
{
    private const int Groups = 2 * DuplicateGroupPrimaries.RepickChunkSize + 1;

    [Fact]
    public async Task DeletingASourceThatLedItsGroups_BindsEachIdListAsOneArrayParameter()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var recorder = new ParameterRecorder();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = tenant };
        (await ctx.LinkedRecords.CountAsync(l => l.IsPrimary && l.DataSource == "source-a")).Should().Be(Groups);
        recorder.Statements.Clear();

        var deleted = await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.FromSource("source-a"), SystemAuditContext.ForService("test"), "data_source=source-a");

        deleted.Should().Be(Groups);
        recorder.MaxParameters.Should().BeLessThan(10, "an id list is bound as one parameter, not one per id");
        recorder.Statements.Where(s => s.Contains("linked_records")).Should()
            .HaveCount(1 + 3 * 3, "one lookup of the groups, then a read, a demote and a promote per chunk")
            .And.OnlyContain(s => s.Contains("= ANY (@"), "every one filters by an array parameter");
        (await ctx.LinkedRecords.CountAsync(l => l.IsPrimary && l.DataSource == "source-b")).Should().Be(Groups);
    }

    private async Task SeedAsync(Guid tenant)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@tid, @slug, 'source-delete-test', true, now(), now());
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
        cmd.Parameters.AddWithValue("slug", $"srcdel-{tenant:N}");
        cmd.Parameters.AddWithValue("groups", Groups);
        cmd.Parameters.AddWithValue("type", RecordTypeKeys.Key(RecordType.SensorGlucose));
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class ParameterRecorder : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];
        public int MaxParameters { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            Statements.Add(command.CommandText);
            MaxParameters = Math.Max(MaxParameters, command.Parameters.Count);
        }
    }
}
