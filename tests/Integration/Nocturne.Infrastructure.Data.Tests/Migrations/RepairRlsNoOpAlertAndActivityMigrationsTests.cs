using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RepairRlsNoOpAlertAndActivityMigrations</c> over rows seeded for two tenants on a real
/// PostgreSQL, under the migrator role and FORCE ROW LEVEL SECURITY, then runs it a second time.
/// </summary>
/// <remarks>
/// The migrations it repairs recorded as applied while touching nothing, so only rows it can see
/// through RLS prove it works. Each tenant holds one row the repair must change and one it must
/// leave alone, per table.
/// </remarks>
public class RepairRlsNoOpAlertAndActivityMigrationsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20260927093001_DropAuthAuditSubjectForeignKeys";

    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private string _migratorConnectionString = string.Empty;

    /// <summary>Per tenant, the repaired rows' <c>resolved_at</c> and <c>deleted_at</c> after the first run.</summary>
    internal Dictionary<Guid, string> AfterFirstRun { get; } = [];

    /// <summary>The same values after the migration was reverted and applied again.</summary>
    internal Dictionary<Guid, string> AfterSecondRun { get; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("rls_noop_repair");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
        foreach (var tenant in new[] { TenantA, TenantB })
            AfterFirstRun[tenant] = await RepairStampsAsync(tenant);

        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(targetMigration: null);
        foreach (var tenant in new[] { TenantA, TenantB })
            AfterSecondRun[tenant] = await RepairStampsAsync(tenant);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private Task<string> RepairStampsAsync(Guid tenantId) => ScalarAsync<string>(tenantId, """
        SELECT concat_ws('|',
            (SELECT resolved_at::text FROM alert_instances WHERE id::text LIKE '%01'),
            (SELECT deleted_at::text FROM heart_rates WHERE "timestamp" IS NULL),
            (SELECT deleted_at::text FROM step_counts WHERE "timestamp" IS NULL));
        """)!;

    internal async Task<T> ScalarAsync<T>(Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();

        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());
            {{TenantRows(TenantA, 'a')}}
            {{TenantRows(TenantB, 'b')}}
            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// An escalating and a triggered instance, and a heart-rate and step-count row with and without
    /// a timestamp. Instance ids ending <c>01</c> are the escalating ones.
    /// </summary>
    private static string TenantRows(Guid tenant, char tag) => $$"""
        SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
        INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                 severity, client_configuration, is_enabled, allow_through_dnd, scope_class,
                                 sort_order, created_at, updated_at)
        VALUES ('{{tag}}0000000-0000-7000-8000-000000000000', '{{tenant}}', 'Rule', 'threshold', '{}', false,
                'warning', '{}', true, false, 'undirected', 0, now(), now());
        INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at)
        VALUES ('{{tag}}0000000-0000-7000-8000-000000000010', '{{tenant}}',
                '{{tag}}0000000-0000-7000-8000-000000000000', now());
        INSERT INTO alert_instances (id, tenant_id, alert_excursion_id, status, triggered_at, snooze_count, is_test)
        VALUES ('{{tag}}0000000-0000-7000-8000-000000000001', '{{tenant}}',
                '{{tag}}0000000-0000-7000-8000-000000000010', 'escalating', now(), 0, false),
               ('{{tag}}0000000-0000-7000-8000-000000000002', '{{tenant}}',
                '{{tag}}0000000-0000-7000-8000-000000000010', 'triggered', now(), 0, false);
        INSERT INTO heart_rates (id, tenant_id, "timestamp", bpm, accuracy, sys_created_at, sys_updated_at)
        VALUES ('{{tag}}0000000-0000-7000-8000-000000000021', '{{tenant}}', NULL, 60, 0, now(), now()),
               ('{{tag}}0000000-0000-7000-8000-000000000022', '{{tenant}}', now(), 70, 0, now(), now());
        INSERT INTO step_counts (id, tenant_id, "timestamp", metric, source, sys_created_at, sys_updated_at)
        VALUES ('{{tag}}0000000-0000-7000-8000-000000000031', '{{tenant}}', NULL, 100, 0, now(), now()),
               ('{{tag}}0000000-0000-7000-8000-000000000032', '{{tenant}}', now(), 200, 0, now(), now());
        """;
}

/// <inheritdoc cref="RepairRlsNoOpAlertAndActivityMigrationsFixture"/>
[Trait("Category", "Integration")]
public class RepairRlsNoOpAlertAndActivityMigrationsTests(RepairRlsNoOpAlertAndActivityMigrationsFixture fixture)
    : IClassFixture<RepairRlsNoOpAlertAndActivityMigrationsFixture>
{
    public static TheoryData<Guid> Tenants =>
        [RepairRlsNoOpAlertAndActivityMigrationsFixture.TenantA, RepairRlsNoOpAlertAndActivityMigrationsFixture.TenantB];

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Escalating_instances_are_resolved_and_others_left_alone(Guid tenant)
    {
        var statuses = await fixture.ScalarAsync<string>(tenant,
            "SELECT string_agg(status || ':' || (resolved_at IS NOT NULL), ',' ORDER BY id) FROM alert_instances;");

        statuses.Should().Be("resolved:true,triggered:false");
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Activity_rows_without_a_timestamp_are_soft_deleted_and_others_left_alone(Guid tenant)
    {
        var heartRates = await fixture.ScalarAsync<string>(tenant,
            "SELECT string_agg(bpm || ':' || (deleted_at IS NOT NULL), ',' ORDER BY id) FROM heart_rates;");
        var stepCounts = await fixture.ScalarAsync<string>(tenant,
            "SELECT string_agg(metric || ':' || (deleted_at IS NOT NULL), ',' ORDER BY id) FROM step_counts;");

        heartRates.Should().Be("60:true,70:false");
        stepCounts.Should().Be("100:true,200:false");
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public void A_second_run_changes_nothing(Guid tenant)
    {
        fixture.AfterFirstRun[tenant].Split('|').Should().HaveCount(3).And.NotContain(string.Empty);
        fixture.AfterSecondRun[tenant].Should().Be(fixture.AfterFirstRun[tenant]);
    }
}
