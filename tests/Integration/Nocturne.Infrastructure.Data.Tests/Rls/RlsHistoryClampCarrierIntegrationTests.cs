using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// End-to-end proof that a history-clamped member reads only the last 24 hours through both
/// acquisition paths: the <see cref="ITenantDbContextFactory"/>, which stamps the clamp from
/// <see cref="ICategoryReadContext"/>, and the request-scoped context, which is pinned before
/// authentication and stamped afterwards the way <c>MemberScopeMiddleware</c> does. Runs the real
/// registration, interceptor and PostgreSQL policy, not the carrier in isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class RlsHistoryClampCarrierIntegrationTests
{
    private const string RecencyTable = "step_counts";

    private readonly RlsCompletenessFixture _fx;

    public RlsHistoryClampCarrierIntegrationTests(RlsCompletenessFixture fx) => _fx = fx;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClampedMember_ReadsOnlyTheLast24Hours_OnBothPaths(bool clamped)
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString, configuration: null);
        services.AddScoped(_ => MockTenantAccessor.Create(tenant).Object);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var scoped = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        if (clamped)
        {
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
            scoped.HistoryClamped = true;
        }

        var expected = clamped ? 1 : 2;

        await using (var leased = await scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>().CreateAsync())
        {
            (await CountAsync(leased, tenant)).Should().Be(expected, "through the factory");
        }

        (await CountAsync(scoped, tenant)).Should().Be(expected, "through the scoped context");
    }

    private static async Task<long> CountAsync(NocturneDbContext context, Guid tenant)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {RecencyTable} WHERE tenant_id = @tid";
            AddParam(cmd, "@tid", tenant);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task SeedAsync(Guid tenant)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();

        await using (var insertTenant = conn.CreateCommand())
        {
            insertTenant.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'history-clamp-test', true, now(), now())
                """;
            AddParam(insertTenant, "@id", tenant);
            AddParam(insertTenant, "@slug", $"clamp-{tenant:N}");
            await insertTenant.ExecuteNonQueryAsync();
        }

        await using (var setTenant = conn.CreateCommand())
        {
            setTenant.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
            AddParam(setTenant, "@tid", tenant.ToString());
            await setTenant.ExecuteScalarAsync();
        }

        await using var insertRows = conn.CreateCommand();
        insertRows.CommandText =
            $"INSERT INTO {RecencyTable} (id, tenant_id, timestamp, metric, source, sys_created_at, sys_updated_at) " +
            "VALUES (gen_random_uuid(), @tid, now(), 0, 0, now(), now()), " +
            "(gen_random_uuid(), @tid, now() - interval '30 hours', 0, 0, now(), now())";
        AddParam(insertRows, "@tid", tenant);
        await insertRows.ExecuteNonQueryAsync();
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
