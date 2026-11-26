using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The lock a by-source glucose delete too wide to recompute inline takes on its readings' duplicate
/// groups before it stamps them, on tables the planner has no statistics for: one statement reads the
/// groups and the next locks their links, each reading the links once and joining its id array to
/// them, so no row is tested against the array and neither reads the links again for each row.
/// </summary>
/// <remarks>
/// It pins the join: each is a hash join on the link column or a nested loop that probes an index of
/// it, with no join filter, so no link is tested against every id. It does not pin the cost, which a
/// hash join over every link of the tenant satisfies, or the order the links are locked in, and it runs
/// each statement once on a connection that does not auto-prepare, so it never sees a generic plan.
/// Which links are locked is pinned by <see cref="TheGroupLock_TakesTheLinksTheReaderCanSee"/>;
/// <see cref="DuplicateFlagEquivalenceTests.ADeferredGlucoseDeleteLeavesNoDeletedReadingAPrimary"/>
/// covers that the delete holds its groups.
/// </remarks>
[Trait("Category", "Integration")]
public class SourceDeleteGroupLockPlanTests(SourceDeletePlanTests.NeverAnalysedDatabase db)
    : IClassFixture<SourceDeletePlanTests.NeverAnalysedDatabase>
{
    private const int Groups = GlucoseStreamFlags.InlineRowLimit + 1;

    /// <summary>Groups for the lock-set case: readings a minute apart, so most are older than the clamp's 24 hours.</summary>
    private const int LockGroups = 2000;

    [Fact]
    public async Task DeletingAWideSource_ReadsTheGroupsThenLocksThem_EachJoiningItsIdArrayOnce()
    {
        var tenant = Guid.NewGuid();
        await db.SeedAsync(tenant, Groups);

        await using var conn = await db.OpenAppConnectionAsync(tenant);
        var recorder = new GroupLockRecorder();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = tenant };

        var deleted = await ctx.AuditedSoftDeleteAsync(
            ctx.SensorGlucose.Selected(RecordSelector.ForSource("source-a")),
            SystemAuditContext.ForService("test"), "data_source=source-a");

        deleted.Should().Be(Groups);
        recorder.Statements.Select(s => s.CommandText.Contains("FOR UPDATE")).Should().Equal(
            [false, true], "the groups are read, then their links locked: "
            + string.Join(" | ", recorder.Statements.Select(s => s.CommandText)));
        foreach (var statement in recorder.Statements)
        {
            var plan = await QueryPlans.ExplainAsync(conn, statement);
            plan.Descendants("Relation Name").Should().Equal(["linked_records"], "the links are read once: " + plan);
            plan.Descendants("Function Name").Should().Equal(["unnest"], "the id array is joined as rows: " + plan);
            plan.Descendants("Filter").Concat(plan.Descendants("Index Cond")).Concat(plan.Descendants("Recheck Cond"))
                .Should().NotContain(c => c.Contains("::uuid[]"), "no row is tested against the id array: " + plan);

            var nodes = PlanNodes(plan).ToList();
            nodes.Select(n => n.TryGetProperty("Join Filter", out var f) ? f.GetString() : null).OfType<string>()
                .Should().BeEmpty("a join condition a hash or an index cannot serve tests every link against every id: " + plan);
            foreach (var join in nodes.Where(n => n.GetProperty("Node Type").GetString()!.Contains("Join")
                         || n.GetProperty("Node Type").GetString() == "Nested Loop"))
            {
                var type = join.GetProperty("Node Type").GetString();
                if (type == "Hash Join")
                {
                    join.TryGetProperty("Hash Cond", out var cond).Should().BeTrue("a hash join names its key: " + plan);
                    cond.GetString().Should().MatchRegex("record_id|canonical_id", "the hash key is the link column: " + plan);
                }
                else
                {
                    type.Should().Be("Nested Loop", "the join is hashed or probed: " + plan);
                    var inner = join.GetProperty("Plans").EnumerateArray()
                        .Single(p => p.GetProperty("Parent Relationship").GetString() == "Inner");
                    PlanNodes(inner).SelectMany(n => n.Descendants("Index Cond")).Should().Contain(
                        c => c.Contains("record_id") || c.Contains("canonical_id"),
                        "a nested loop probes an index of the link column for each id: " + plan);
                }
            }
        }
    }

    private static IEnumerable<JsonElement> PlanNodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var n in PlanNodes(item))
                yield return n;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out _))
                yield return element;
            foreach (var p in element.EnumerateObject())
            foreach (var n in PlanNodes(p.Value))
                yield return n;
        }
    }

    /// <summary>
    /// The lock takes every link the reader can see of the groups of the readings it is given. Links are
    /// not clamped (<c>ShareDataCategories</c> leaves them out of the 24 hour clamp), so a clamped member
    /// locks the links of groups older than its window too, and a share, which links are hidden from,
    /// locks none. Counted from another transaction, which skips the locked links.
    /// </summary>
    [Theory]
    [InlineData("owner", 2 * LockGroups)]
    [InlineData("clamped", 2 * LockGroups)]
    [InlineData("share", 0)]
    public async Task TheGroupLock_TakesTheLinksTheReaderCanSee(string reader, int locked)
    {
        var tenant = Guid.NewGuid();
        await db.SeedAsync(tenant, LockGroups);
        Guid[] ids;
        await using (var owner = await db.OpenAppConnectionAsync(tenant))
            ids = [.. await IdsAsync(owner, "SELECT id FROM sensor_glucose WHERE data_source = 'source-a'")];

        await using var conn = await db.OpenAppConnectionAsync(tenant);
        if (reader != "owner")
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = reader == "clamped"
                ? "SELECT set_config('app.history_clamped', 'true', false)"
                : "SELECT set_config('app.is_share', 'true', false), set_config('app.visible_categories', 'glucose.read', false)";
            await cmd.ExecuteNonQueryAsync();
        }

        if (reader == "clamped")
            (await IdsAsync(conn, "SELECT id FROM sensor_glucose WHERE data_source = 'source-a'")).Should()
                .HaveCountLessThan(LockGroups, "the clamp hides the older readings from this reader");

        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .Options) { TenantId = tenant };
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        using (await GlucoseStreamFlags.DeferIfWideAsync(ctx, GlucoseStreamFlags.InlineRowLimit + 1, default))
            await DuplicateGroupPrimaries.LockGroupsOfAsync(ctx, RecordType.SensorGlucose, ids, default);

        await using var other = await db.OpenAppConnectionAsync(tenant);
        await using var otherTransaction = await other.BeginTransactionAsync();
        var all = await IdsAsync(other, "SELECT id FROM linked_records");
        var free = await IdsAsync(other, "SELECT id FROM linked_records FOR UPDATE SKIP LOCKED");
        all.Should().HaveCount(2 * LockGroups);
        (all.Count - free.Count).Should().Be(locked);
    }

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            ids.Add(r.GetGuid(0));
        return ids;
    }

    /// <summary>The <c>linked_records</c> statements a delete runs before it stamps its first reading.</summary>
    private sealed class GroupLockRecorder : DbCommandInterceptor
    {
        private bool _stamping;

        public List<NpgsqlCommand> Statements { get; } = [];

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
            _stamping |= command.CommandText.TrimStart().StartsWith("UPDATE sensor_glucose", StringComparison.Ordinal);
            if (_stamping || !command.CommandText.Contains("linked_records", StringComparison.Ordinal))
                return;

            var copy = new NpgsqlCommand(command.CommandText);
            foreach (NpgsqlParameter p in command.Parameters)
                copy.Parameters.Add(p.Clone());
            Statements.Add(copy);
        }
    }
}
