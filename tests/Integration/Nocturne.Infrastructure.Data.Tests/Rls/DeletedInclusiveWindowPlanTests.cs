using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A window read that takes soft-deleted rows walks <c>ix_&lt;table&gt;_tenant_timestamp</c>, which
/// unlike the list indexes is not partial on <c>deleted_at</c>; and the live profile activation reads
/// still walk <c>ix_profile_activations_tenant_start_timestamp</c> now it holds deleted rows too. The
/// query the repository sends is captured and explained as <c>nocturne_app</c>. The read tenant is
/// older than four others, so walking the instance-wide timestamp index would pass their rows first.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DeletedInclusiveWindowPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const int Limit = 100;
    private const int OtherTenants = 4;

    private const string Columns = "(id, tenant_id, timestamp, legacy_id, deleted_at, {0}sys_created_at, sys_updated_at)";
    private const string Values =
        "gen_random_uuid(), @tid, now() - {0} - make_interval(mins => g), 'l-' || g, CASE WHEN g % 25 = 0 THEN now() END, {1}now(), now()";

    [Theory]
    [InlineData("bg_checks", "glucose, ", "100, ")]
    [InlineData("notes", "text, is_announcement, ", "'n', false, ")]
    [InlineData("device_events", "event_type, ", "'SiteChange', ")]
    [InlineData("bolus_calculations", "", "")]
    public Task DeletedInclusiveWindowRead_WalksTheTenantTimestampIndex(string table, string columns, string values) =>
        AssertIndexWalkAsync(
            table,
            string.Format(Columns, columns),
            values,
            $"ix_{table}_tenant_timestamp",
            async factory => (await DeletedFlagsOfWindowAsync(table, factory))
                .Should().HaveCount(Limit).And.Contain(true, "the read takes soft-deleted rows"));

    [Fact]
    public Task ProfileActivations_LiveStartingAfterReadWalksTheTenantStartIndex() =>
        AssertProfileActivationsAsync(async repository =>
            (await repository.GetStartingAfterAsync(DateTime.UtcNow.AddDays(-60), Limit)).Should().HaveCount(Limit));

    [Fact]
    public Task ProfileActivations_LiveOverlappingReadWalksTheTenantStartIndex() =>
        AssertProfileActivationsAsync(async repository =>
            (await repository.GetOverlappingAsync(DateTime.UtcNow.AddDays(-60), DateTime.UtcNow, Limit)).Should().HaveCount(Limit));

    private Task AssertProfileActivationsAsync(Func<ProfileActivationRepository, Task> read) =>
        AssertIndexWalkAsync(
            "profile_activations",
            "(id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms, snapshot_is_customized, legacy_id, deleted_at, sys_created_at, sys_updated_at)",
            "'Active', 'User', 'Default', 100, 0, false, ",
            "ix_profile_activations_tenant_start_timestamp",
            factory => read(new ProfileActivationRepository(
                factory, new Mock<IAuditContext>().Object, NullLogger<ProfileActivationRepository>.Instance)),
            "gen_random_uuid(), @tid, now() - {0} - make_interval(mins => g), {1}'l-' || g, CASE WHEN g % 25 = 0 THEN now() END, now(), now()");

    private static async Task<List<bool>> DeletedFlagsOfWindowAsync(string table, TestNocturneDbContextFactory factory)
    {
        var linker = new Mock<IDuplicateLinker>().Object;
        var audit = new Mock<IAuditContext>().Object;
        return table switch
        {
            "bg_checks" => [.. (await new BGCheckRepository(factory, linker, audit, NullLogger<BGCheckRepository>.Instance)
                .GetIncludingDeletedAsync(null, null, Limit, descending: true)).Select(r => r.Deleted)],
            "notes" => [.. (await new NoteRepository(factory, linker, audit, NullLogger<NoteRepository>.Instance)
                .GetIncludingDeletedAsync(null, null, Limit, descending: true)).Select(r => r.Deleted)],
            "device_events" => [.. (await new DeviceEventRepository(factory, linker, audit, NullLogger<DeviceEventRepository>.Instance)
                .GetIncludingDeletedAsync(null, null, Limit, descending: true)).Select(r => r.Deleted)],
            _ => [.. (await new BolusCalculationRepository(factory, linker, audit, NullLogger<BolusCalculationRepository>.Instance)
                .GetIncludingDeletedAsync(null, null, Limit, descending: true)).Select(r => r.Deleted)],
        };
    }

    private async Task AssertIndexWalkAsync(
        string table, string columns, string values, string index,
        Func<TestNocturneDbContextFactory, Task> read, string valuesTemplate = Values)
    {
        for (var i = 0; i < OtherTenants; i++)
            await PlanTestSupport.SeedAsync(fx, Guid.NewGuid(), table, columns, string.Format(valuesTemplate, "interval '0 days'", values), Rows);

        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(fx, tenant, table, columns, string.Format(valuesTemplate, "interval '30 days'", values), Rows);

        await using var conn = await fx.OpenAppConnectionAsync();
        await QueryPlans.SetTenantAsync(conn, tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        await read(new TestNocturneDbContextFactory(ctx));

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Equal(
            [index], $"the tenant's rows are read off the tenant-led index, not past every tenant's; plan: {plan}");
    }
}
