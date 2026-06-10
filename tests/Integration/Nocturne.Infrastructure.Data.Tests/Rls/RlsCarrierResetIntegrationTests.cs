using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// End-to-end proof that the carrier-reset chokepoint prevents the share-RLS background lockout:
/// a pooled <see cref="NocturneDbContext"/> that last served a public share is reused by a normal
/// read, and because <c>CarrierResettingDbContextFactory</c> resets <c>IsShareContext</c> on the
/// lease, the <c>TenantConnectionInterceptor</c> writes <c>app.is_share='false'</c> and the reused
/// connection is NOT denied a non-categorized (share-hidden) tenant-scoped table. Runs against the
/// real pooled factory, interceptor and PostgreSQL RLS policy, not the C# carrier in isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class RlsCarrierResetIntegrationTests
{
    // A non-categorized (share-hidden) tenant-scoped table: under a stale is_share='true' it is
    // denied outright, which is the lockout this fix prevents.
    private const string HiddenTable = "body_weights";

    private readonly RlsCompletenessFixture _fx;

    public RlsCarrierResetIntegrationTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task ReusedPooledContext_AfterAShare_DoesNotLockOutANormalRead()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        // The real production registration: pooled factory + carrier-reset decorator + interceptor.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        // Lease 1 — a public share granted a category that does not unlock the hidden table. Its
        // own scope returns the context to the pool (still carrying IsShareContext=true) before
        // lease 2 acquires, even if the share assertion throws.
        NocturneDbContext pooledContext;
        await using (var shareContext = await factory.CreateDbContextAsync())
        {
            pooledContext = shareContext;
            shareContext.TenantId = tenant;
            shareContext.IsShareContext = true;
            shareContext.VisibleCategories = "stepcount.read";
            await shareContext.Database.OpenConnectionAsync();
            (await CountAsync(shareContext, HiddenTable, tenant)).Should().Be(0,
                "a public share must not see a hidden (non-categorized) table");
        }

        // Lease 2 — a normal read that reuses the same pooled object. The decorator must have
        // cleared the share marker; without it the stale is_share='true' would deny the owner
        // their own data.
        await using var ownerContext = await factory.CreateDbContextAsync();
        ownerContext.Should().BeSameAs(pooledContext,
            "this single-threaded acquire-after-dispose must reuse the same pooled object, so the "
            + "test exercises carrier reset on a reused context rather than a fresh one");
        ownerContext.IsShareContext.Should().BeFalse(
            "the chokepoint must clear a prior share's marker before the pooled context is reused");
        ownerContext.TenantId = tenant;
        await ownerContext.Database.OpenConnectionAsync();
        (await CountAsync(ownerContext, HiddenTable, tenant)).Should().Be(1,
            "a reused pooled context must not carry a stale share lockout into a normal read");
    }

    // Counts via the EF-managed connection so the count runs on the same session the
    // TenantConnectionInterceptor configured when EF opened the connection.
    private static async Task<long> CountAsync(NocturneDbContext context, string table, Guid tenant)
    {
        var connection = context.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE tenant_id = @tid";
        AddParam(cmd, "@tid", tenant);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task SeedAsync(Guid tenant)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();

        await using (var insertTenant = conn.CreateCommand())
        {
            insertTenant.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'carrier-reset-test', true, now(), now())
                """;
            AddParam(insertTenant, "@id", tenant);
            AddParam(insertTenant, "@slug", $"carrier-{tenant:N}");
            await insertTenant.ExecuteNonQueryAsync();
        }

        // Row inserts run under the tenant so the multitenant RLS policy admits them.
        await using (var setTenant = conn.CreateCommand())
        {
            setTenant.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
            AddParam(setTenant, "@tid", tenant.ToString());
            await setTenant.ExecuteScalarAsync();
        }

        await using (var insertRow = conn.CreateCommand())
        {
            insertRow.CommandText =
                $"INSERT INTO {HiddenTable} (id, tenant_id, mills, weight_kg, sys_created_at, sys_updated_at) " +
                "VALUES (gen_random_uuid(), @tid, 0, 0, now(), now())";
            AddParam(insertRow, "@tid", tenant);
            await insertRow.ExecuteNonQueryAsync();
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
