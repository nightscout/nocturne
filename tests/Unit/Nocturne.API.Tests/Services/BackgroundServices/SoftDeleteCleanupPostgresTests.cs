using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// <see cref="SoftDeleteCleanupService"/> on PostgreSQL under the runtime role and Row Level
/// Security: each tenant's own retention row decides how long its soft-deleted records survive.
/// </summary>
[Trait("Category", "Integration")]
public class SoftDeleteCleanupPostgresTests(SoftDeleteCleanupPostgresTests.Database database)
    : IClassFixture<SoftDeleteCleanupPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("soft_delete_cleanup");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Factory(Func<NocturneDbContext> create) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(create());
    }

    private NocturneDbContext Context() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options);

    private async Task SeedTenantAsync(Guid tenant, int? retentionDays, int deletedDaysAgo)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@tenant, @slug, 'soft-delete-cleanup-test', true, now(), now());
            SELECT set_config('app.current_tenant_id', @tenant::text, false);
            INSERT INTO tenant_data_retention_config (id, tenant_id, soft_delete_retention_days, created_at, updated_at)
            SELECT gen_random_uuid(), @tenant, @days, now(), now() WHERE @days IS NOT NULL;
            INSERT INTO body_weights (id, tenant_id, mills, weight_kg, sys_created_at, sys_updated_at, deleted_at)
            VALUES (gen_random_uuid(), @tenant, 0, 70, now(), now(), now() - make_interval(days => @ago));
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@tenant", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"soft-delete-{tenant:N}"));
        cmd.Parameters.Add(new NpgsqlParameter("@days", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = (object?)retentionDays ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@ago", deletedDaysAgo));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> BodyWeightCountAsync(Guid tenant)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant::text, false);
            SELECT count(*) FROM body_weights WHERE tenant_id = @tenant;
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@tenant", tenant));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        await reader.ReadAsync();
        return reader.GetInt64(0);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_AppliesEachTenantsOwnRetention()
    {
        var longRetention = Guid.NewGuid();
        var defaultRetention = Guid.NewGuid();
        await SeedTenantAsync(longRetention, retentionDays: 60, deletedDaysAgo: 40);
        await SeedTenantAsync(defaultRetention, retentionDays: null, deletedDaysAgo: 40);

        var service = new SoftDeleteCleanupService(
            new Factory(Context),
            new ConfigurationBuilder().Build(),
            NullLogger<SoftDeleteCleanupService>.Instance);

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        (await BodyWeightCountAsync(longRetention)).Should().Be(1);
        (await BodyWeightCountAsync(defaultRetention)).Should().Be(0);
    }
}
