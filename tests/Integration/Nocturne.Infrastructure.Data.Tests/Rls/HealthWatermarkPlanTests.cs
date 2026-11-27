using Nocturne.Infrastructure.Data.Repositories.Health;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A connector's resume watermark on heart rates and step counts
/// (<c>ObservedHealthSeriesRepository.GetLatestTimestampAsync</c>) is an index-only scan of the tenant and
/// source index. The read the repository sends is captured and explained as <c>nocturne_app</c>, on a tenant
/// among four others whose rows carry the same sources, with one row in seven deleted, the watched source one
/// row in fifty, and the table vacuumed so the planner knows its pages are all visible: an index that does not
/// hold the soft-delete filter cannot answer the read without the heap.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class HealthWatermarkPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const int OtherTenants = 4;
    private const string Source = "watched";

    private const string SourceColumn =
        "CASE g % 50 WHEN 0 THEN 'watched' WHEN 1 THEN 'libre' WHEN 2 THEN NULL ELSE 'dexcom' END";
    private const string SomeDeleted = "CASE WHEN g % 7 = 0 THEN now() END";

    [Theory]
    [InlineData("heart_rates", "bpm, accuracy, ", "70, 1, ", "ix_heart_rates_tenant_source_timestamp")]
    [InlineData("step_counts", "metric, source, ", "10, 1, ", "ix_step_counts_tenant_source_timestamp")]
    public async Task TheWatermarkReadIsAnIndexOnlyScan(string table, string columns, string values, string index)
    {
        var columnList = $"(id, tenant_id, timestamp, {columns}data_source, deleted_at, sys_created_at, sys_updated_at)";
        var valueList = $"gen_random_uuid(), @tid, now() - make_interval(mins => g), {values}{SourceColumn}, {SomeDeleted}, now(), now()";

        for (var i = 0; i < OtherTenants; i++)
            await PlanTestSupport.SeedAsync(fx, Guid.NewGuid(), table, columnList, valueList, Rows);

        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(fx, tenant, table, columnList, valueList, Rows);
        await VacuumAsync(table);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };
        var factory = new TestNocturneDbContextFactory(ctx);

        var latest = table == "heart_rates"
            ? await new HeartRateRepository(factory, TimeProvider.System).GetLatestTimestampAsync(Source)
            : await new StepCountRepository(factory, TimeProvider.System).GetLatestTimestampAsync(Source);
        latest.Should().NotBeNull("a watched source with no rows would let any plan pass");

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        var because = plan.ToString();
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", because);
        plan.Descendants("Node Type").Should().Contain("Index Only Scan", because);
        plan.Descendants("Index Name").Should().OnlyContain(name => name == index, because);
    }

    private async Task VacuumAsync(string table)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM (ANALYZE) {table}";
        await cmd.ExecuteNonQueryAsync();
    }
}
