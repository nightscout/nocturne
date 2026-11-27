using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The previous-reading read a derived trend makes per evaluated reading ranges
/// <c>ix_sensor_glucose_tenant_timestamp</c> over the 15 minutes before the reading, since it has to
/// see the duplicate copies the partial list indexes leave out, and probes
/// <c>ix_linked_records_tenant_type_id</c> for the reading's own duplicate group. The query the
/// repository sends is captured and explained as <c>nocturne_app</c>. Four other tenants hold readings
/// over the same minutes, so ranging the cross-tenant timestamp index would read theirs too.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SensorGlucoseStreamPredecessorPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const int OtherTenants = 4;

    [Fact]
    public async Task The_previous_reading_read_ranges_the_tenant_timestamp_index()
    {
        for (var i = 0; i < OtherTenants; i++)
            await SeedReadingsAsync(Guid.NewGuid());
        var tenant = Guid.NewGuid();
        await SeedReadingsAsync(tenant);
        await SeedLinksAsync(tenant);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var repository = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(ctx),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
        var at = DateTime.UtcNow.AddHours(-2);
        var reading = new SensorGlucose
        {
            Id = Guid.NewGuid(), Timestamp = at, Mgdl = 100, DataSource = "xdrip", Device = "g7",
        };
        (await repository.GetStreamPredecessorAsync(reading, at.AddMinutes(-15))).Should().NotBeNull();

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Contain("ix_sensor_glucose_tenant_timestamp", plan.ToString());
    }

    /// <summary>
    /// The recent readings a windowed alert condition reads range the same index over the 180 minutes
    /// before the newest reading.
    /// </summary>
    [Fact]
    public async Task The_recent_readings_read_ranges_the_tenant_timestamp_index()
    {
        for (var i = 0; i < OtherTenants; i++)
            await SeedReadingsAsync(Guid.NewGuid());
        var tenant = Guid.NewGuid();
        await SeedReadingsAsync(tenant);
        await SeedLinksAsync(tenant);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var repository = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(ctx),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
        var at = DateTime.UtcNow.AddHours(-4);
        var newest = new SensorGlucose
        {
            Id = Guid.NewGuid(), Timestamp = at, Mgdl = 100, DataSource = "xdrip", Device = "g7",
        };
        (await repository.GetStreamSeriesAsync(newest, at.AddMinutes(-180))).Should().NotBeEmpty();

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Contain("ix_sensor_glucose_tenant_timestamp", plan.ToString());
    }

    private Task SeedReadingsAsync(Guid tenant) =>
        PlanTestSupport.SeedAsync(
            fx, tenant, "sensor_glucose",
            "(id, tenant_id, timestamp, mgdl, data_source, device, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, date_trunc('minute', now()) - make_interval(mins => g), 100 + g % 50, (ARRAY['dexcom-connector','xdrip'])[1 + g % 2], 'g7', now(), now()",
            Rows);

    /// <summary>One duplicate group per pair of the tenant's readings, as a busy two-uploader tenant has.</summary>
    private async Task SeedLinksAsync(Guid tenant)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO linked_records (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary, sys_created_at)
                SELECT gen_random_uuid(), @tid,
                       md5(@tid::text || (r.n / 2)::text)::uuid,
                       'sensorglucose', r.id, (extract(epoch FROM r.timestamp) * 1000)::bigint, r.data_source, r.n % 2 = 0, now()
                FROM (SELECT id, timestamp, data_source, row_number() OVER (ORDER BY timestamp) AS n
                      FROM sensor_glucose WHERE tenant_id = @tid) r
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = "ANALYZE linked_records";
        await analyze.ExecuteNonQueryAsync();
    }
}
