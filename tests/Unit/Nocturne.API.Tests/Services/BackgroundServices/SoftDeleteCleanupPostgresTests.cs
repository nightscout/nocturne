using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Nocturne.Infrastructure.Data.Repositories;

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

    /// <summary>
    /// The purge deletes a deduplicated row's links in the statement that deletes the row, so
    /// nothing is left for an orphan sweep. The live row's link, and a link of another type that
    /// happens to share the purged id, stay.
    /// </summary>
    [Fact]
    public async Task PurgeExpiredRecordsAsync_DeletesTheLinksOfThePurgedRowsWithThem()
    {
        var tenant = Guid.NewGuid();
        await SeedTenantAsync(tenant, retentionDays: null, deletedDaysAgo: 40);
        var expired = Guid.CreateVersion7();
        var live = Guid.CreateVersion7();
        await using (var seed = Context())
        {
            seed.TenantId = tenant;
            seed.Boluses.Add(new BolusEntity
            {
                Id = expired, TenantId = tenant, Insulin = 1, Timestamp = DateTime.UtcNow, DeletedAt = DateTime.UtcNow.AddDays(-40),
            });
            seed.Boluses.Add(new BolusEntity { Id = live, TenantId = tenant, Insulin = 2, Timestamp = DateTime.UtcNow });
            seed.LinkedRecords.Add(Link(tenant, "bolus", expired));
            seed.LinkedRecords.Add(Link(tenant, "bolus", live));
            seed.LinkedRecords.Add(Link(tenant, "carbintake", expired));
            await seed.SaveChangesAsync();
        }

        var service = new SoftDeleteCleanupService(
            new Factory(Context),
            new TenantRepository(new Factory(Context)),
            new ConfigurationBuilder().Build(),
            NullLogger<SoftDeleteCleanupService>.Instance);

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        await using var db = Context();
        db.TenantId = tenant;
        (await db.Boluses.IgnoreQueryFilters().Where(b => b.TenantId == tenant).Select(b => b.Id).ToListAsync())
            .Should().Equal(live);
        (await db.LinkedRecords.Select(l => new { l.RecordType, l.RecordId }).ToListAsync())
            .Should().BeEquivalentTo(
            [
                new { RecordType = "bolus", RecordId = live },
                new { RecordType = "carbintake", RecordId = expired },
            ]);
    }

    private static LinkedRecordEntity Link(Guid tenant, string recordType, Guid recordId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        CanonicalId = Guid.CreateVersion7(),
        RecordType = recordType,
        RecordId = recordId,
        SourceTimestamp = 0,
        DataSource = "test",
        IsPrimary = true,
        SysCreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task PurgeExpiredRecordsAsync_AppliesEachTenantsOwnRetention()
    {
        var longRetention = Guid.NewGuid();
        var defaultRetention = Guid.NewGuid();
        await SeedTenantAsync(longRetention, retentionDays: 60, deletedDaysAgo: 40);
        await SeedTenantAsync(defaultRetention, retentionDays: null, deletedDaysAgo: 40);

        var service = new SoftDeleteCleanupService(
            new Factory(Context),
            new TenantRepository(new Factory(Context)),
            new ConfigurationBuilder().Build(),
            NullLogger<SoftDeleteCleanupService>.Instance);

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        (await BodyWeightCountAsync(longRetention)).Should().Be(1);
        (await BodyWeightCountAsync(defaultRetention)).Should().Be(0);
    }
}
