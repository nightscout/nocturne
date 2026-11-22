using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Statements that filter by an id list, run past their tenth execution on one connection of the
/// runtime pool, which auto-prepares, on tables the planner has no statistics for. Planned for its
/// array (<see cref="ArrayFilterPlans"/>), each is given the array as a value, which PostgreSQL hashes;
/// generic-planned, it is given a parameter, which a filter searches end to end for every row it tests.
/// The plan read is the one each execution ran, sent back by <c>auto_explain</c>.
/// </summary>
/// <remarks>
/// It pins the plan, not the time: the cost of a search by row is the tenant's rows times the ids, which
/// these few thousand rows do not make slow.
/// </remarks>
[Trait("Category", "Integration")]
public class ArrayFilterPlanTests(SourceDeletePlanTests.NeverAnalysedDatabase db)
    : IClassFixture<SourceDeletePlanTests.NeverAnalysedDatabase>, IAsyncLifetime
{
    private const int Groups = 2 * NocturneDbContext.SystemTimestampGroupSize + 1;

    /// <summary>A delete of <see cref="Groups"/> readings stamps three chunks, so four deletes run the stamp twelve times.</summary>
    private const int Deletes = 4;

    private const int Runs = 12;

    public Task InitializeAsync() => db.SendPlansAsNoticesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ADeleteRepeatedOnOneConnection_PlansEveryArrayFilterForItsArray()
    {
        var tenant = Guid.NewGuid();
        await db.SeedAsync(tenant, Groups);
        await using var source = RuntimePool();
        await using var conn = await OpenAsync(source, tenant);
        var plans = Plans(conn);

        for (var run = 0; run < Deletes; run++)
        {
            await using var ctx = Context(conn, tenant);
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            (await ctx.AuditedSoftDeleteAsync(
                ctx.SensorGlucose.Selected(RecordSelector.ForSource("source-a")),
                SystemAuditContext.ForService("test"), "data_source=source-a")).Should().Be(Groups);
            await transaction.RollbackAsync();
        }

        var prepared = await PreparedAsync(conn);
        prepared.Should().Contain(
            p => p.Statement.Contains("SET deleted_at") && p.Custom + p.Generic > 5,
            "the stamp ran twelve times, so it was prepared and planned past PostgreSQL's five custom plans");
        prepared.Where(p => p.Statement.Contains("= ANY (") || p.Statement.Contains("unnest("))
            .Should().OnlyContain(p => p.Generic == 0, "every statement that filters by an array is planned for it");
        var stamp = plans.Last(p => p.Contains("SET deleted_at", StringComparison.Ordinal));
        PlanOf(stamp).Should().Contain("id = ANY ('{", "the last stamp was given its ids as a value: " + stamp)
            .And.NotMatchRegex(@"= ANY \(\$\d", "no array is a parameter: " + stamp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheGroupLock_RunTwelveTimes_SearchesItsArrayByRowOnlyGenericPlanned(bool planned)
    {
        var tenant = Guid.NewGuid();
        await db.SeedAsync(tenant, Groups);
        await using var source = RuntimePool();
        await using var conn = await OpenAsync(source, tenant);
        Guid[] canonicals;
        await using (var read = new NpgsqlCommand("SELECT DISTINCT canonical_id FROM linked_records", conn))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            var list = new List<Guid>();
            while (await reader.ReadAsync())
                list.Add(reader.GetGuid(0));
            canonicals = [.. list];
        }

        var plans = Plans(conn);
        for (var run = 0; run < Runs; run++)
        {
            await using var ctx = Context(conn, tenant);
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            if (planned)
                await ctx.PlanForEachArrayAsync(canonicals.Length, default);
            await DuplicateGroupPrimaries.LockGroupsAsync(ctx, RecordType.SensorGlucose, canonicals, default);
            await transaction.RollbackAsync();
        }

        var lockPlan = PlanOf(plans.Last(p => p.Contains("canonical_id = ANY", StringComparison.Ordinal)));
        var prepared = (await PreparedAsync(conn)).Single(p => p.Statement.Contains("canonical_id = ANY"));
        if (planned)
        {
            prepared.Generic.Should().Be(0);
            lockPlan.Should().Contain("canonical_id = ANY ('{", "the twelfth lock was given its canonicals as a value: " + lockPlan);
        }
        else
        {
            prepared.Generic.Should().BePositive("PostgreSQL keeps a generic plan once its five custom plans cost no less");
            lockPlan.Should().MatchRegex(@"Filter: .*canonical_id = ANY \(\$\d",
                "a scan of the tenant tests each link against the parameter array: " + lockPlan);
        }
    }

    /// <summary>The runtime pool as the API builds it, held to one connection so every run shares a backend.</summary>
    private NpgsqlDataSource RuntimePool() => PostgresRuntimeOptions.BuildRuntimeDataSource(
        new PostgreSqlConfiguration { ConnectionString = db.AppConnectionString, MaxPoolSize = 1 });

    private static async Task<NpgsqlConnection> OpenAsync(NpgsqlDataSource source, Guid tenant)
    {
        var conn = await source.OpenConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);
        return conn;
    }

    private static NocturneDbContext Context(NpgsqlConnection conn, Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options) { TenantId = tenant };

    /// <summary>The plans <c>auto_explain</c> sends back on <paramref name="conn"/> from now on, in order.</summary>
    private static List<string> Plans(NpgsqlConnection conn)
    {
        var plans = new List<string>();
        conn.Notice += (_, e) =>
        {
            if (e.Notice.MessageText.Contains("plan:", StringComparison.Ordinal))
                plans.Add(e.Notice.MessageText);
        };
        return plans;
    }

    /// <summary>The plan's nodes, without the statement text and parameter values logged ahead of them.</summary>
    private static string PlanOf(string notice)
    {
        var lines = notice.Split('\n');
        return string.Join('\n', lines.SkipWhile(l => !l.Contains("(cost=", StringComparison.Ordinal)));
    }

    private static async Task<List<(string Statement, long Generic, long Custom)>> PreparedAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT statement, generic_plans, custom_plans FROM pg_prepared_statements", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var prepared = new List<(string, long, long)>();
        while (await reader.ReadAsync())
            prepared.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return prepared;
    }
}
