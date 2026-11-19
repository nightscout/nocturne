using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The keyed lookup and delete on notes, device events and BG checks read by
/// <c>(data_source, sync_identifier)</c> under the tenant and soft-delete filters, which the
/// partial unique <c>ix_&lt;table&gt;_tenant_source_sync_id</c> answers. The query the repository
/// actually sends is captured and explained as <c>nocturne_app</c>, over enough rows that a scan
/// would be the planner's choice without the index.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SyncKeyLookupPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const string DataSource = "aaps";

    [Fact]
    public Task Notes_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "notes",
            "(id, tenant_id, timestamp, text, is_announcement, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'n', false, 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await Notes(factory).FindBySyncIdentifierAsync(DataSource, key));

    [Fact]
    public Task DeviceEvents_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "device_events",
            "(id, tenant_id, timestamp, event_type, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 'SiteChange', 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await DeviceEvents(factory).FindBySyncIdentifierAsync(DataSource, key));

    [Fact]
    public Task BGChecks_KeyedLookupProbesTheSyncKeyIndex() =>
        AssertLookupAsync(
            "bg_checks",
            "(id, tenant_id, timestamp, glucose, data_source, sync_identifier, sys_created_at, sys_updated_at)",
            "gen_random_uuid(), @tid, now() - make_interval(mins => g), 100, 'aaps', 'sync-' || g, now(), now()",
            async (factory, key) => await BGChecks(factory).FindBySyncIdentifierAsync(DataSource, key));

    private static NoteRepository Notes(TestNocturneDbContextFactory factory) =>
        new(factory, new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
            NullLogger<NoteRepository>.Instance);

    private static DeviceEventRepository DeviceEvents(TestNocturneDbContextFactory factory) =>
        new(factory, new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);

    private static BGCheckRepository BGChecks(TestNocturneDbContextFactory factory) =>
        new(factory, new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
            NullLogger<BGCheckRepository>.Instance);

    private async Task AssertLookupAsync(
        string table, string columns, string values, Func<TestNocturneDbContextFactory, string, Task<object?>> lookup)
    {
        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(fx, tenant, table, columns, values, Rows);

        await using var conn = await fx.OpenAppConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);

        var capture = new PlanTestSupport.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        (await lookup(new TestNocturneDbContextFactory(ctx), "sync-7777")).Should().NotBeNull();

        var plan = await PlanTestSupport.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Equal(
            [$"ix_{table}_tenant_source_sync_id"], $"the sync key has a partial unique index to probe; plan: {plan}");
    }
}
