using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The sweep's owed-work read of a tenant's excursions is answered by
/// <c>ix_alert_excursions_tenant_owed</c>, which holds only the rows that owe work, not every
/// excursion the tenant ever had. The query the repository actually sends is captured and explained
/// as <c>nocturne_app</c>, over enough settled rows that the tenant's other indexes would be the
/// planner's choice without it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class AlertOwedTransitionPlanTests(RlsCompletenessFixture fx)
{
    private const int Settled = 20_000;
    private const int Owed = 5;
    private const string Index = "ix_alert_excursions_tenant_owed";

    [Fact]
    public async Task OwedRead_IsServedByThePartialIndex()
    {
        var tenant = Guid.NewGuid();
        var rule = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(
            fx, tenant, "alert_rules",
            "(id, tenant_id, name, condition_type, condition_params, sort_order, severity)",
            $"'{rule}', @tid, 'owed', 'threshold', '{{}}', 0, 'warning'",
            rows: 1);
        await SeedExcursionsAsync(tenant, rule);

        await using var conn = await fx.OpenAppConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);

        var capture = new PlanTestSupport.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var repository = new AlertRepository(
            new TestNocturneDbContextFactory(ctx), NullLogger<AlertRepository>.Instance);
        var owed = await repository.GetOwedTransitionsAsync(DateTime.UtcNow, CancellationToken.None);

        owed.Should().HaveCount(Owed);
        owed.Should().OnlyContain(t => t.Kind == ExcursionTransitionType.ExcursionOpened);

        var plan = await PlanTestSupport.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Index Name").Should().Contain(Index, plan.ToString());
        SeqScannedTables(plan).Should().NotContain("alert_excursions", plan.ToString());
    }

    private static IEnumerable<string> SeqScannedTables(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("Node Type", out var type) && type.GetString() == "Seq Scan"
                    && node.TryGetProperty("Relation Name", out var relation))
                    yield return relation.GetString()!;
                foreach (var property in node.EnumerateObject())
                foreach (var table in SeqScannedTables(property.Value))
                    yield return table;
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                foreach (var table in SeqScannedTables(item))
                    yield return table;
                break;
        }
    }

    private async Task SeedExcursionsAsync(Guid tenant, Guid rule)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at, ended_at, opened_owed_at)
                SELECT gen_random_uuid(), @tid, @rid,
                       now() - make_interval(mins => g + 1), now() - make_interval(mins => g),
                       CASE WHEN g <= {Owed} THEN now() - interval '1 hour' END
                FROM generate_series(1, {Settled}) g
                """;
            insert.Parameters.AddWithValue("tid", tenant);
            insert.Parameters.AddWithValue("rid", rule);
            await insert.ExecuteNonQueryAsync();
        }

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = "ANALYZE alert_excursions";
        await analyze.ExecuteNonQueryAsync();
    }
}
