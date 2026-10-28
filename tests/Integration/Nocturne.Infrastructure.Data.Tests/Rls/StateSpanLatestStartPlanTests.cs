using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The latest-by-source reads probe <c>ix_state_spans_tenant_source_category_start</c> once per
/// category. The query the repository actually sends is captured and explained as
/// <c>nocturne_app</c>, over enough rows that a scan would be the planner's choice without the index.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanLatestStartPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 20_000;
    private const string Source = "aaps";
    private const string Index = "ix_state_spans_tenant_source_category_start";

    [Fact]
    public Task Activity_ProbesTheIndexOncePerCategory() =>
        AssertProbesAsync(
            (repository, source) => repository.GetLatestActivityTimestampAsync(source),
            arms: 4);

    [Fact]
    public Task NonActivity_ProbesTheIndexOncePerCategory() =>
        AssertProbesAsync(
            (repository, source) => repository.GetLatestNonActivityTimestampAsync(source),
            arms: 6);

    private async Task AssertProbesAsync(
        Func<StateSpanRepository, string, Task<DateTime?>> read, int arms)
    {
        var tenant = Guid.NewGuid();
        await PlanTestSupport.SeedAsync(
            fx, tenant, "state_spans",
            "(id, tenant_id, category, state, start_timestamp, source, created_at, updated_at)",
            $"gen_random_uuid(), @tid, (ARRAY['PumpMode','PumpConnectivity','Override','Profile','Exercise','Illness','Travel','DataExclusion','TemporaryTarget','Motion'])[1 + g % 10], 's', now() - make_interval(mins => g), '{Source}', now(), now()",
            Rows);

        await using var conn = await fx.OpenAppConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, tenant);

        var capture = new PlanTestSupport.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var repository = new StateSpanRepository(
            new TestNocturneDbContextFactory(ctx),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance,
            TimeProvider.System);
        (await read(repository, Source)).Should().NotBeNull();

        var plan = await PlanTestSupport.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Equal(
            Enumerable.Repeat(Index, arms),
            $"each category is its own probe of the tenant, source, category index; plan: {plan}");
    }
}
