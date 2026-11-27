using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A source's count on the connector detail page reads every record table as four index ranges
/// (<c>SourceFilter.Parts</c>). The count the table registry sends is captured and explained as
/// <c>nocturne_app</c>, on a tenant among four others whose rows carry the same sources, with some
/// rows deleted and the source held under each handle: each part has to read an index, or the page
/// reads the tenant's whole partition of the table.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SourceCountPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const int OtherTenants = 4;
    private const string Source = "target";

    private const string SourceColumn =
        "CASE g % 50 WHEN 0 THEN 'target' WHEN 1 THEN 'libre' WHEN 2 THEN NULL WHEN 3 THEN 'zzz' WHEN 4 THEN 'aaa' ELSE 'dexcom' END";
    private const string DeviceColumn = "CASE WHEN g % 50 BETWEEN 2 AND 4 THEN 'target' ELSE 'rig-' || (g % 3) END";
    private const string SomeDeleted = "CASE WHEN g % 7 = 0 THEN now() END";
    private const string Minutes = "now() - make_interval(mins => g)";

    [Theory]
    [InlineData(typeof(HeartRateEntity), "heart_rates", "timestamp", Minutes, "bpm, accuracy, ", "70, 1, ", "ix_heart_rates_tenant_source_timestamp")]
    [InlineData(typeof(StepCountEntity), "step_counts", "timestamp", Minutes, "metric, source, ", "10, 1, ", "ix_step_counts_tenant_source_timestamp")]
    [InlineData(typeof(PumpSnapshotEntity), "pump_snapshots", "timestamp", Minutes, "", "", "ix_pump_snapshots_tenant_source_timestamp")]
    [InlineData(typeof(BasalScheduleEntity), "basal_schedules", "timestamp", Minutes, "profile_name, entries_json, ", "'Default', '[]', ", "ix_basal_schedules_tenant_source_timestamp")]
    [InlineData(typeof(ProfileActivationEntity), "profile_activations", "start_timestamp", Minutes,
        "phase, origin, base_profile_name, percentage, timeshift_ms, snapshot_is_customized, ", "'Active', 'User', 'Default', 100, 0, false, ",
        "ix_profile_activations_tenant_source_timestamp")]
    [InlineData(typeof(BodyWeightEntity), "body_weights", "mills", "(extract(epoch from now()) * 1000)::bigint - g * 60000",
        "weight_kg, ", "80, ", "ix_body_weights_tenant_source")]
    public async Task SourceCount_ReadsAnIndexForEveryPart(
        Type entity, string table, string timeColumn, string time, string columns, string values, string dataSourceIndex)
    {
        var columnList = $"(id, tenant_id, {timeColumn}, {columns}data_source, device, deleted_at, sys_created_at, sys_updated_at)";
        var valueList = $"gen_random_uuid(), @tid, {time}, {values}{SourceColumn}, {DeviceColumn}, {SomeDeleted}, now(), now()";

        for (var i = 0; i < OtherTenants; i++)
            await PlanTestSupport.SeedAsync(fx, Guid.NewGuid(), table, columnList, valueList, Rows);

        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(fx, tenant, table, columnList, valueList, Rows);

        var expected = await LiveRowsOfSourceAsync(tenant, table);
        expected.Should().BeGreaterThan(0, "a source with no rows would let any plan pass");

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var count = await V4RecordTables.All.Single(t => t.Entity == entity).CountAsync(ctx, Source, CancellationToken.None);
        count.Should().Be(expected);

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        var scanned = plan.Descendants("Index Name").ToList();
        scanned.Should().HaveCountGreaterThanOrEqualTo(4, $"each part of the count reads an index of its own; plan: {plan}");
        scanned.Should().OnlyContain(
            name => name == dataSourceIndex || name == $"ix_{table}_tenant_device_source", plan.ToString());
    }

    private async Task<long> LiveRowsOfSourceAsync(Guid tenant, string table)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE deleted_at IS NULL AND (data_source = @s OR device = @s)";
        cmd.Parameters.AddWithValue("s", Source);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
