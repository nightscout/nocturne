using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Nocturne.API.Integration.Tests.Health;

[Trait("Category", "Integration")]
public sealed class GoogleHealthReconciliationTests(GoogleHealthPostgresFixture fixture)
    : IClassFixture<GoogleHealthPostgresFixture>, IAsyncLifetime
{
    private string adminConnection = string.Empty;
    private string connectionString = string.Empty;
    private readonly string databaseName = "google_health_test_" + Guid.NewGuid().ToString("N");
    private readonly string roleName = "google_health_app_" + Guid.NewGuid().ToString("N");
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly DateTimeOffset from = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        adminConnection = fixture.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin))
            await command.ExecuteNonQueryAsync();
        connectionString = new NpgsqlConnectionStringBuilder(adminConnection)
            { Database = databaseName, Pooling = false }.ConnectionString;
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var migrations = db.GetService<IMigrationsAssembly>();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var entry in migrations.Migrations.Where(entry => entry.Key.Contains("GoogleHealthReconciliationStaging")))
        {
            var migration = migrations.CreateMigration(entry.Value, db.Database.ProviderName!);
            foreach (var command in generator.Generate(migration.UpOperations, db.Model))
                await db.Database.ExecuteSqlRawAsync(command.CommandText);
        }
        db.Tenants.AddRange(
            new TenantEntity { Id = tenantId, Slug = "google-test", DisplayName = "Synthetic", IsActive = true },
            new TenantEntity { Id = otherTenantId, Slug = "google-other", DisplayName = "Synthetic other", IsActive = true });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync($"CREATE ROLE {roleName} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        await db.Database.ExecuteSqlRawAsync($"GRANT USAGE ON SCHEMA public TO {roleName}");
        await db.Database.ExecuteSqlRawAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {roleName}");
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)", admin))
            await command.ExecuteNonQueryAsync();
        await using (var command = new NpgsqlCommand($"DROP ROLE IF EXISTS {roleName}", admin))
            await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Staged_writer_preserves_empty_types_and_reconciles_all_native_tables()
    {
        await using var db = Context();
        foreach (var tenant in new[] { tenantId, otherTenantId })
        {
            db.TenantId = tenant;
            foreach (var identifier in new[] { "retained", "stale", "other-source", "outside" })
            {
                var timestamp = identifier == "outside" ? from.AddDays(2).UtcDateTime : from.AddHours(6).UtcDateTime;
                var source = identifier == "other-source" ? "manual" : GoogleHealthReadingWriter.Source;
                db.HeartRates.Add(new HeartRateEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Bpm = 60, DataSource = source, SyncIdentifier = identifier });
                db.StepCounts.Add(new StepCountEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Metric = 42, DataSource = source, SyncIdentifier = identifier });
                db.BodyWeights.Add(new BodyWeightEntity { Id = Guid.NewGuid(), Mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(), WeightKg = 70, DataSource = source, SyncIdentifier = identifier });
                db.SleepSessions.Add(new SleepSessionEntity { Id = Guid.NewGuid(), StartTime = timestamp.AddHours(-8), EndTime = timestamp, Source = identifier == "other-source" ? "Manual" : "Google", SourceApp = "Google Health", OriginalId = identifier });
            }
            await db.SaveChangesAsync();
        }
        await UseTenantAsync(db, tenantId);
        var writer = Writer(db);
        string[] types = ["heart-rate", "steps", "weight", "sleep"];
        var emptyRun = await writer.BeginReconciliationAsync(types, from, from.AddDays(1), default);
        foreach (var type in types) await writer.StageReconciliationIdsAsync(emptyRun, type, ["", " "], default);
        await writer.CompleteReconciliationAsync(emptyRun, default);
        Assert.Equal(4, await db.HeartRates.CountAsync());
        Assert.Equal(4, await db.StepCounts.CountAsync());
        Assert.Equal(4, await db.BodyWeights.CountAsync());
        Assert.Equal(4, await db.SleepSessions.CountAsync());

        var mixedRun = await writer.BeginReconciliationAsync(types, from, from.AddDays(1), default);
        await writer.StageReconciliationIdsAsync(mixedRun, "steps", ["retained"], default);
        await writer.CompleteReconciliationAsync(mixedRun, default);
        Assert.Equal(3, await db.StepCounts.CountAsync());
        Assert.Equal(4, await db.HeartRates.CountAsync());
        Assert.Equal(4, await db.BodyWeights.CountAsync());
        Assert.Equal(4, await db.SleepSessions.CountAsync());

        var run = await writer.BeginReconciliationAsync(types, from, from.AddDays(1), default);
        foreach (var type in types) await writer.StageReconciliationIdsAsync(run, type, ["retained", "retained"], default);
        await writer.CompleteReconciliationAsync(run, default);
        Assert.Equal(3, await db.HeartRates.CountAsync());
        Assert.Equal(3, await db.StepCounts.CountAsync());
        Assert.Equal(3, await db.BodyWeights.CountAsync());
        Assert.Equal(3, await db.SleepSessions.CountAsync());
        Assert.False(await db.StepCounts.IgnoreQueryFilters().Where(record => record.TenantId == tenantId && record.SyncIdentifier == "stale")
            .Select(record => EF.Property<bool>(record, "DeletedByUser")).SingleAsync());
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_ids"));
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_runs"));
        await UseTenantAsync(db, otherTenantId);
        Assert.Equal(4, await db.HeartRates.CountAsync());
        Assert.Equal(4, await db.StepCounts.CountAsync());
        Assert.Equal(4, await db.BodyWeights.CountAsync());
        Assert.Equal(4, await db.SleepSessions.CountAsync());
    }

    [Fact]
    public async Task Staging_enforces_rls_batches_ids_and_expires_only_abandoned_tenant_runs()
    {
        var counter = new InsertCounter();
        await using var db = Context(counter);
        await UseTenantAsync(db, tenantId);
        var writer = Writer(db);
        var run = await writer.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        await writer.StageReconciliationIdsAsync(run, "steps", Enumerable.Range(0, 10000).Select(index => "id-" + index).ToArray(), default);
        Assert.Equal(10, counter.Inserts);
        Assert.Equal(10000, await CountAsync(db, "google_health_reconciliation_ids"));
        await writer.StageReconciliationIdsAsync(run, "steps", ["id-0", "id-0"], default);
        Assert.Equal(10000, await CountAsync(db, "google_health_reconciliation_ids"));
        var enabled = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM pg_class
            WHERE relname IN ('google_health_reconciliation_ids', 'google_health_reconciliation_runs')
              AND relrowsecurity AND relforcerowsecurity
            """).SingleAsync();
        Assert.Equal(2, enabled);
        var expired = DateTime.UtcNow.AddDays(-8);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE google_health_reconciliation_runs SET created_at = {expired} WHERE id = {run}");
        await UseTenantAsync(db, otherTenantId);
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_runs"));
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_ids"));
        await writer.StageReconciliationIdsAsync(run, "steps", ["cross-tenant"], default);
        await writer.AbandonReconciliationAsync(run, default);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO google_health_reconciliation_ids (run_id, tenant_id, data_type, identifier)
            VALUES ({run}, {tenantId}, 'steps', 'forbidden')
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        var otherRun = await writer.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        await writer.StageReconciliationIdsAsync(otherRun, "steps", ["other-tenant"], default);
        await UseTenantAsync(db, tenantId);
        Assert.Equal(10000, await CountAsync(db, "google_health_reconciliation_ids"));
        var fresh = await writer.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_ids"));
        Assert.Equal(1, await CountAsync(db, "google_health_reconciliation_runs"));
        await writer.AbandonReconciliationAsync(fresh, default);
        await UseTenantAsync(db, otherTenantId);
        Assert.Equal(1, await CountAsync(db, "google_health_reconciliation_ids"));
        await writer.AbandonReconciliationAsync(otherRun, default);
        Assert.Equal(0, await CountAsync(db, "google_health_reconciliation_ids"));
    }

    private NocturneDbContext Context(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(connectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new NocturneDbContext(options.Options);
    }

    private async Task UseTenantAsync(NocturneDbContext db, Guid tenant)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync($"SET ROLE {roleName}");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.current_tenant_id', {tenant.ToString()}, false)");
        db.TenantId = tenant;
    }

    private static GoogleHealthReadingWriter Writer(NocturneDbContext db) => new(
        Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(), Mock.Of<IBodyWeightService>(),
        Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);

    private static Task<int> CountAsync(NocturneDbContext db, string table) =>
        db.Database.SqlQueryRaw<int>($"SELECT count(*)::integer AS \"Value\" FROM {table}").SingleAsync();

    private sealed class InsertCounter : DbCommandInterceptor
    {
        public int Inserts { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO google_health_reconciliation_ids")) Inserts++;
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class GoogleHealthPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("NOCTURNE_TEST_POSTGRES_ADMIN") ?? string.Empty;
        if (!string.IsNullOrEmpty(ConnectionString)) return;
        container = new PostgreSqlBuilder().WithImage("postgres:17.6").Build();
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (container is not null) await container.DisposeAsync();
    }
}