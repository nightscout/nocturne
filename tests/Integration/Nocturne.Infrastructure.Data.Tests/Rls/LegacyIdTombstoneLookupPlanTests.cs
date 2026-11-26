using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The legacy-id lookups that read soft-deleted rows, which the partial unique
/// <c>(tenant_id, legacy_id)</c> index leaves out.
/// <see cref="SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync{TEntity}(NocturneDbContext, HashSet{string}, CancellationToken)"/>
/// reads user tombstones as well as live rows, and without <c>ix_&lt;table&gt;_tenant_legacy_id_user_deleted</c>
/// the planner walks every row the tenant owns (#1270). The isValid:false upload paths read every
/// deleted row, and without <c>ix_&lt;table&gt;_tenant_legacy_id_deleted</c> the planner walks every
/// tenant's deleted rows, so those tests seed four other tenants as large as the reader. The query EF actually sends is captured and explained as
/// <c>nocturne_app</c>, so a change to either the LINQ or the index family that loses the probe fails here.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class LegacyIdTombstoneLookupPlanTests(RlsCompletenessFixture fx)
{
    private const int LiveRows = 20_000;
    private const int SweptRows = 1_000;
    private const int UserDeletedRows = 1_000;
    private const int OtherTenants = 4;

    private const string SensorGlucoseColumns =
        "(id, tenant_id, timestamp, mgdl, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)";

    private const string SensorGlucoseValues =
        "gen_random_uuid(), @tid, now() - make_interval(mins => g), 100, {0}, {1}, {2}, now(), now()";

    [Fact]
    public Task SensorGlucose_LookupProbesBothLegacyIdIndexes() =>
        AssertLookupAsync<SensorGlucoseEntity>("sensor_glucose", SensorGlucoseColumns, SensorGlucoseValues);

    [Fact]
    public Task TempBasals_LookupProbesBothLegacyIdIndexes() =>
        AssertLookupAsync<TempBasalEntity>(
            "temp_basals",
            "(id, tenant_id, start_timestamp, rate, origin, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 1.0, 'Algorithm', {0}, {1}, {2}, now(), now()");

    [Fact]
    public Task SensorGlucose_DeletedLookupProbesTheDeletedRowIndex() =>
        AssertDeletedLookupAsync(
            "sensor_glucose", SensorGlucoseColumns, SensorGlucoseValues,
            (factory, ids) => new SensorGlucoseRepository(
                    factory, new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
                    NullLogger<SensorGlucoseRepository>.Instance)
                .GetDeletedLegacyIdsAsync(ids));

    [Theory]
    [InlineData("aps_snapshots")]
    [InlineData("pump_snapshots")]
    [InlineData("uploader_snapshots")]
    public Task Snapshots_DeletedLookupProbesTheDeletedRowIndex(string table) =>
        AssertDeletedLookupAsync(
            table,
            table == "aps_snapshots"
                ? "(id, tenant_id, timestamp, aps_system, enacted, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)"
                : "(id, tenant_id, timestamp, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)",
            table == "aps_snapshots"
                ? "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'AndroidAPS', true, {0}, {1}, {2}, now(), now()"
                : "gen_random_uuid(), @tid, now() - make_interval(mins => g), {0}, {1}, {2}, now(), now()",
            (factory, ids) =>
            {
                var audit = new Mock<IAuditContext>().Object;
                return table switch
                {
                    "aps_snapshots" => new ApsSnapshotRepository(factory, audit, NullLogger<ApsSnapshotRepository>.Instance)
                        .GetDeletedLegacyIdsAsync(ids),
                    "pump_snapshots" => new PumpSnapshotRepository(factory, audit, NullLogger<PumpSnapshotRepository>.Instance)
                        .GetDeletedLegacyIdsAsync(ids),
                    _ => new UploaderSnapshotRepository(factory, audit, NullLogger<UploaderSnapshotRepository>.Instance)
                        .GetDeletedLegacyIdsAsync(ids),
                };
            });

    [Theory]
    [InlineData("Bolus", "boluses", "timestamp, insulin, automatic", "1.0, false")]
    [InlineData("TempBasal", "temp_basals", "start_timestamp, origin", "'Algorithm'")]
    [InlineData("CarbIntake", "carb_intakes", "timestamp, carbs", "10")]
    [InlineData("BGCheck", "bg_checks", "timestamp, glucose", "100")]
    [InlineData("DeviceEvent", "device_events", "timestamp, event_type", "'SiteChange'")]
    [InlineData("BolusCalculation", "bolus_calculations", "timestamp", null)]
    [InlineData(
        "ProfileActivation", "profile_activations",
        "start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms, snapshot_is_customized",
        "'Active', 'User', 'Default', 100, 0, false")]
    public Task DecomposedTreatmentTables_DeletedLookupProbesAnIndexHoldingEveryDeletedRow(
        string model, string table, string columns, string? values) =>
        AssertDeletedLookupAsync(
            table,
            $"(id, tenant_id, {columns}, legacy_id, deleted_at, deleted_by_user, sys_created_at, sys_updated_at)",
            $"gen_random_uuid(), @tid, now() - make_interval(mins => g), {(values is null ? "" : values + ", ")}{{0}}, {{1}}, {{2}}, now(), now()",
            async (factory, ids) =>
            {
                await using var db = await factory.CreateAsync();
                return (await DecomposedTreatmentTables.All.Single(t => t.Model.Name == model)
                    .DeletedAsync(db, [.. ids], CancellationToken.None)).ToHashSet();
            });

    private async Task AssertLookupAsync<TEntity>(string table, string columns, string valuesTemplate)
        where TEntity : class, IV4Entity
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, table, columns, valuesTemplate);

        var lookedUp = Enumerable.Range(1, 50).Select(i => $"live-{i * 300}")
            .Concat(Enumerable.Range(1, 50).Select(i => $"swept-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"user-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"absent-{i}"))
            .ToHashSet(StringComparer.Ordinal);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var blocks = await ctx.GetBlockingLegacyIdsAsync<TEntity>(lookedUp);

        blocks.Held.Should().BeEquivalentTo(
            lookedUp.Where(id => id.StartsWith("live-") || id.StartsWith("user-")),
            "live rows and user tombstones block re-creation; system sweeps and unknown ids do not");
        blocks.DeletedByUser.Should().BeEquivalentTo(lookedUp.Where(id => id.StartsWith("user-")));

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        var indexes = plan.Descendants("Index Name").ToList();
        var nodes = plan.Descendants("Node Type").ToList();

        nodes.Should().NotContain("Seq Scan", plan.ToString());
        indexes.Should().BeEquivalentTo(
            [$"ix_{table}_tenant_legacy_id", $"ix_{table}_tenant_legacy_id_user_deleted"],
            $"each arm of the live-or-user-tombstone OR has a partial index to probe; plan: {plan}");
    }

    private async Task AssertDeletedLookupAsync(
        string table, string columns, string valuesTemplate,
        Func<TestNocturneDbContextFactory, IReadOnlyCollection<string>, Task<IReadOnlySet<string>>> read)
    {
        for (var i = 0; i < OtherTenants; i++)
            await SeedAsync(Guid.NewGuid(), table, columns, valuesTemplate);

        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, table, columns, valuesTemplate);

        var lookedUp = Enumerable.Range(1, 50).Select(i => $"live-{i * 300}")
            .Concat(Enumerable.Range(1, 50).Select(i => $"swept-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"user-{i * 20}"))
            .Concat(Enumerable.Range(1, 50).Select(i => $"absent-{i}"))
            .ToHashSet(StringComparer.Ordinal);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        (await read(new TestNocturneDbContextFactory(ctx), lookedUp)).Should().BeEquivalentTo(
            lookedUp.Where(id => id.StartsWith("swept-") || id.StartsWith("user-")),
            "a row deleted by the system holds its id as a user tombstone does");

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);

        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Equal(
            [$"ix_{table}_tenant_legacy_id_deleted"],
            $"the tenant's deleted rows are probed by legacy id, not walked across every tenant; plan: {plan}");
    }

    private async Task SeedAsync(Guid tenant, string table, string columns, string valuesTemplate)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.SeedTenantAsync(tenant, $"tomb-{tenant:N}", "tombstone-plan-test");

        await conn.PinTenantAsync(tenant);

        (string Prefix, int Count, string DeletedAt, string ByUser)[] batches =
        [
            ("live", LiveRows, "NULL", "false"),
            ("swept", SweptRows, "now()", "false"),
            ("user", UserDeletedRows, "now()", "true"),
        ];

        foreach (var (prefix, count, deletedAt, byUser) in batches)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {table} {columns}
                SELECT {string.Format(valuesTemplate, $"'{prefix}-' || g", deletedAt, byUser)}
                FROM generate_series(1, {count}) g
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = $"ANALYZE {table}";
        await analyze.ExecuteNonQueryAsync();
    }
}
