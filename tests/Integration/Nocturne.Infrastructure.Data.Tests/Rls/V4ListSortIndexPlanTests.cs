using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The dashboard's first read of sensor glucose is a newest-first list with no range, which
/// <c>ix_sensor_glucose_tenant_timestamp_id</c> answers by reading n index entries. The latest-before
/// read on a snapshot table walks <c>ix_pump_snapshots_tenant_timestamp_id</c> the same way. The
/// query the repository sends is captured and explained as <c>nocturne_app</c>. The pump tenant is
/// older than four others, so walking the cross-tenant timestamp index would skip their rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class V4ListSortIndexPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const int Limit = 12;
    private const int OtherTenants = 4;

    [Fact]
    public Task SensorGlucose_UnrangedNewestFirstListWalksTheSortIndex() =>
        AssertIndexWalkAsync(
            "sensor_glucose",
            "(id, tenant_id, timestamp, mgdl, data_source, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 100, 'dexcom', now(), now()",
            "ix_sensor_glucose_tenant_timestamp_id",
            async (factory, now) =>
            {
                var repository = new SensorGlucoseRepository(
                    factory,
                    new Mock<IDeduplicationService>().Object,
                    new Mock<IAuditContext>().Object,
                    NullLogger<SensorGlucoseRepository>.Instance,
                    TimeProvider.System);
                (await repository.GetAsync(null, null, null, null, Limit, 0, descending: true))
                    .Should().HaveCount(Limit);
            });

    [Fact]
    public Task PumpSnapshots_LatestBeforeWalksTheSortIndex() =>
        AssertIndexWalkAsync(
            "pump_snapshots",
            "(id, tenant_id, timestamp, data_source, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - interval '30 days' - make_interval(mins => g), 'aaps', now(), now()",
            "ix_pump_snapshots_tenant_timestamp_id",
            async (factory, now) =>
            {
                var repository = new PumpSnapshotRepository(
                    factory, new Mock<IAuditContext>().Object, NullLogger<PumpSnapshotRepository>.Instance);
                (await repository.GetLatestBeforeAsync(now)).Should().NotBeNull();
            },
            otherTenantsValues: "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'aaps', now(), now()");

    private async Task AssertIndexWalkAsync(
        string table, string columns, string values, string index,
        Func<TestNocturneDbContextFactory, DateTime, Task> read, string? otherTenantsValues = null)
    {
        if (otherTenantsValues is not null)
        {
            for (var i = 0; i < OtherTenants; i++)
                await PlanTestSupport.SeedAsync(fx, Guid.NewGuid(), table, columns, otherTenantsValues, Rows);
        }

        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(fx, tenant, table, columns, values, Rows);

        await using var conn = await fx.OpenAppConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);

        var capture = new PlanTestSupport.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        await read(new TestNocturneDbContextFactory(ctx), DateTime.UtcNow.AddMinutes(1));

        var plan = await PlanTestSupport.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Index Name").Should().Contain(index, plan.ToString());
        plan.Descendants("Node Type").Should().NotContain(
            ["Seq Scan", "Sort", "Incremental Sort"], plan.ToString());
    }
}
