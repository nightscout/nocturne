using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Proves how a background purge must obtain tenant reach when it hard-deletes from a
/// <c>FORCE ROW LEVEL SECURITY</c> table, and that the alternative fails silently.
///
/// EF opens and closes the connection around each command, so a <c>set_config</c> issued as its
/// own command is discarded by <c>TenantConnectionInterceptor</c>'s reset before the next command
/// runs. The following DELETE then evaluates
/// <c>tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid</c> against
/// NULL, matches nothing, and reports success having deleted zero rows — the purge appears to
/// work and never removes anything.
///
/// <see cref="RlsPinningExtensions.CreateTenantPinnedContextAsync"/> sets the carrier the
/// interceptor writes when the connection opens, so the GUC is present for the DELETE itself.
///
/// Each case seeds its own tenant, as <see cref="RlsCarrierResetIntegrationTests"/> does:
/// <see cref="RlsCompletenessFixture"/> is seedless in the sense that it stands up no rows of
/// its own, not that its collection forbids them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class AuditPurgePinningIntegrationTests
{
    private const string AuditTable = "mutation_audit_log";

    private readonly RlsCompletenessFixture _fx;

    public AuditPurgePinningIntegrationTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task PinnedContext_DeletesExpiredAuditRows_WhereSetConfigCommandDeletesNothing()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, rows: 3);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        var cutoff = DateTime.UtcNow.AddDays(-90);

        // The broken form: the pin is issued as its own command, so the connection closes and
        // resets the GUC before the DELETE runs.
        await using (var unpinned = await factory.CreateDbContextAsync())
        {
            await unpinned.Database.ExecuteSqlRawAsync(
                "SELECT set_config('app.current_tenant_id', {0}, false)", [tenant.ToString()]);

            var deleted = await unpinned.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {AuditTable} WHERE created_at < {{0}}", [cutoff]);

            deleted.Should().Be(0,
                "a set_config issued as its own EF command does not survive to the DELETE, so "
                + "RLS matches nothing and the purge silently removes no rows");
        }

        (await CountAsync(tenant)).Should().Be(3, "the unpinned DELETE must not have removed anything");

        // The production primitive both retention sweeps call. It pins internally, so the GUC is
        // present when EF opens the connection for the DELETE.
        var purged = await factory.PurgeOlderThanAsync(
            tenant, AuditTable, "created_at", cutoff);

        purged.Should().Be(3, "the shared purge pins the tenant and so actually deletes");
        (await CountAsync(tenant)).Should().Be(0, "the expired rows must be gone");
    }

    /// <summary>
    /// Deliberately seeds more expired rows than <c>batchSize</c>, so the batching loop must
    /// iterate. With a batch large enough to swallow the fixture in one statement the loop body
    /// runs once and its exit condition is never exercised — the sweep that clears a backlog of
    /// hundreds of thousands of rows would then be uncovered.
    /// </summary>
    [Fact]
    public async Task PurgeOlderThanAsync_IteratesBatches_SparingRecentRowsAndOtherTenants()
    {
        var tenant = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        await SeedAsync(tenant, rows: 9);
        await SeedAsync(neighbour, rows: 3);
        await SeedRecentRowAsync(tenant);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        var purged = await factory.PurgeOlderThanAsync(
            tenant, AuditTable, "created_at", DateTime.UtcNow.AddDays(-90), batchSize: 4);

        purged.Should().Be(9, "every expired row must be removed across three batches");
        (await CountAsync(tenant)).Should().Be(1, "the row inside the retention window survives");
        (await CountAsync(neighbour)).Should().Be(3, "the purge must not reach another tenant");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RejectsACutoffThatIsNotInThePast()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        // A retention window of zero or fewer days resolves to a cutoff at or after now, which
        // would delete rows written moments earlier.
        var act = () => factory.PurgeOlderThanAsync(
            Guid.NewGuid(), AuditTable, "created_at", DateTime.UtcNow.AddDays(1));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RejectsABatchSizeThatCannotMakeProgress()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        var act = () => factory.PurgeOlderThanAsync(
            Guid.NewGuid(), AuditTable, "created_at", DateTime.UtcNow.AddDays(-1), batchSize: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("mutation_audit_log; DROP TABLE tenants", "created_at")]
    [InlineData("mutation_audit_log", "created_at) --")]
    [InlineData("Mutation_Audit_Log", "created_at")]
    public async Task PurgeOlderThanAsync_RejectsNonIdentifiers(string table, string column)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        var act = () => factory.PurgeOlderThanAsync(
            Guid.NewGuid(), table, column, DateTime.UtcNow);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private async Task SeedRecentRowAsync(Guid tenant)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {AuditTable} (id, tenant_id, entity_type, action, created_at) "
            + "VALUES (gen_random_uuid(), @tid, 'SensorGlucose', 'update', now())";
        AddParam(cmd, "@tid", tenant);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(Guid tenant)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {AuditTable} WHERE tenant_id = @tid";
        AddParam(cmd, "@tid", tenant);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task SeedAsync(Guid tenant, int rows)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();

        await using (var insertTenant = conn.CreateCommand())
        {
            insertTenant.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'audit-purge-test', true, now(), now())
                """;
            AddParam(insertTenant, "@id", tenant);
            AddParam(insertTenant, "@slug", $"audit-purge-{tenant:N}");
            await insertTenant.ExecuteNonQueryAsync();
        }

        // Row inserts run under the tenant so the multitenant RLS policy admits them.
        await SetTenantAsync(conn, tenant);

        for (var i = 0; i < rows; i++)
        {
            await using var insertRow = conn.CreateCommand();
            insertRow.CommandText =
                $"INSERT INTO {AuditTable} (id, tenant_id, entity_type, action, created_at) "
                + "VALUES (gen_random_uuid(), @tid, 'SensorGlucose', 'update', now() - interval '200 days')";
            AddParam(insertRow, "@tid", tenant);
            await insertRow.ExecuteNonQueryAsync();
        }
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
        AddParam(cmd, "@tid", tenant.ToString());
        await cmd.ExecuteScalarAsync();
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
