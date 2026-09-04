using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
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
    private static readonly TimeSpan NinetyDays = TimeSpan.FromDays(90);

    private readonly RlsCompletenessFixture _fx;

    public AuditPurgePinningIntegrationTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task PinnedContext_DeletesExpiredAuditRows_WhereSetConfigCommandDeletesNothing()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, rows: 3);

        var factory = BuildFactory();
        var cutoff = DateTime.UtcNow - NinetyDays;

        // The broken form: the pin is issued as its own command, so the connection closes and
        // resets the GUC before the DELETE runs. Bounded by tenant_id so that if the pin ever
        // did carry, this could not reach another test's rows in the shared container.
        await using (var unpinned = await factory.CreateDbContextAsync())
        {
            await unpinned.Database.ExecuteSqlRawAsync(
                "SELECT set_config('app.current_tenant_id', {0}, false)", [tenant.ToString()]);

            var deleted = await unpinned.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {AuditTable} WHERE tenant_id = {{1}} AND created_at < {{0}}",
                [cutoff, tenant]);

            deleted.Should().Be(0,
                "a set_config issued as its own EF command does not survive to the DELETE, so "
                + "RLS matches nothing and the purge silently removes no rows");
        }

        (await CountAsync(tenant)).Should().Be(3, "the unpinned DELETE must not have removed anything");

        // The production primitive both retention sweeps call. It pins internally, so the GUC is
        // present when EF opens the connection for the DELETE.
        var purged = await factory.PurgeOlderThanAsync(tenant, AuditTable, "created_at", NinetyDays);

        purged.Should().Be(3, "the shared purge pins the tenant and so actually deletes");
        (await CountAsync(tenant)).Should().Be(0, "the expired rows must be gone");
    }

    /// <summary>
    /// Deliberately seeds more expired rows than <c>batchSize</c>, so the batching loop must
    /// iterate. With a batch large enough to swallow the fixture in one statement the loop body
    /// runs once and its exit condition is never exercised — the sweep that clears a backlog of
    /// over a million rows would then be uncovered.
    /// </summary>
    [Fact]
    public async Task PurgeOlderThanAsync_IteratesBatches_SparingRecentRowsAndOtherTenants()
    {
        var tenant = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        await SeedAsync(tenant, rows: 9);
        await SeedAsync(neighbour, rows: 3);
        await SeedRecentRowAsync(tenant);

        var purged = await BuildFactory().PurgeOlderThanAsync(
            tenant, AuditTable, "created_at", NinetyDays, batchSize: 4);

        purged.Should().Be(9, "every expired row must be removed across three batches");
        (await CountAsync(tenant)).Should().Be(1, "the row inside the retention window survives");
        (await CountAsync(neighbour)).Should().Be(3, "the purge must not reach another tenant");
    }

    /// <summary>
    /// The <c>tenant_id</c> predicate is a backstop for a target RLS does not bound, so RLS
    /// cannot witness it — every behavioural assertion here passes with the predicate removed.
    /// Asserting the emitted SQL is what keeps it from being deleted as dead weight.
    /// </summary>
    [Fact]
    public async Task PurgeOlderThanAsync_BoundsBothTheDeleteAndTheSubSelectByTenant()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant, rows: 1);

        var capture = new CapturingInterceptor();
        await BuildFactory(capture).PurgeOlderThanAsync(tenant, AuditTable, "created_at", NinetyDays);

        var delete = capture.Commands.Should()
            .ContainSingle(c => c.StartsWith("DELETE FROM", StringComparison.Ordinal)).Subject;

        delete.Split("tenant_id = ").Length.Should().Be(3,
            "both the outer DELETE and the ctid sub-select must be bounded by tenant_id, so the "
            + "purge cannot cross tenants on a table RLS does not force");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PurgeOlderThanAsync_RejectsANonPositiveWindow(int days)
    {
        // A zero-day window is the boundary that matters: the cutoff it produces is a hair
        // earlier than the moment the purge runs, so an absolute-cutoff guard would admit it and
        // the sweep would delete the tenant's entire audit table.
        var act = () => BuildFactory().PurgeOlderThanAsync(
            Guid.NewGuid(), AuditTable, "created_at", TimeSpan.FromDays(days));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RejectsABatchSizeThatCannotMakeProgress()
    {
        var act = () => BuildFactory().PurgeOlderThanAsync(
            Guid.NewGuid(), AuditTable, "created_at", NinetyDays, batchSize: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("mutation_audit_log; DROP TABLE tenants", "created_at")]
    [InlineData("mutation_audit_log", "created_at) --")]
    [InlineData("Mutation_Audit_Log", "created_at")]
    [InlineData("mutation_audit_log\n", "created_at")]
    [InlineData("mutation_audit_log", "created_at\n")]
    public async Task PurgeOlderThanAsync_RejectsNonIdentifiers(string table, string column)
    {
        var act = () => BuildFactory().PurgeOlderThanAsync(
            Guid.NewGuid(), table, column, NinetyDays);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private IDbContextFactory<NocturneDbContext> BuildFactory(IInterceptor? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();

        if (extra is null)
        {
            services.AddPostgreSqlInfrastructure(_fx.AppConnectionString);
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        }

        var dataSource = new NpgsqlDataSourceBuilder(_fx.AppConnectionString).Build();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor(), extra)
            .Options;
        return new PlainContextFactory(options);
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

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
        AddParam(cmd, "@tid", tenant.ToString());
        await cmd.ExecuteScalarAsync();
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private sealed class PlainContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private sealed class CapturingInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, ct);
        }
    }
}
