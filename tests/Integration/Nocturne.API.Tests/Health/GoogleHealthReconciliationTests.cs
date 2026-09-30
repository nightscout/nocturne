using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Integration.Tests.Health;

[Trait("Category", "Integration")]
public sealed class GoogleHealthReconciliationTests(GoogleHealthPostgresFixture fixture)
    : IClassFixture<GoogleHealthPostgresFixture>, IAsyncLifetime
{
    private string connectionString = string.Empty;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly DateTimeOffset from = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        connectionString = fixture.Database.SuperuserConnectionString;
        await using var db = Context();
        db.Tenants.AddRange(
            new TenantEntity { Id = tenantId, Slug = $"google-test-{tenantId:N}", DisplayName = "Synthetic", IsActive = true },
            new TenantEntity { Id = otherTenantId, Slug = $"google-other-{otherTenantId:N}", DisplayName = "Synthetic other", IsActive = true });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Writer_uses_existing_native_tables_and_preserves_empty_types_and_other_tenants()
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
        await UseTenantAsync(db, otherTenantId);
        Assert.Equal(4, await db.HeartRates.CountAsync());
        Assert.Equal(4, await db.StepCounts.CountAsync());
        Assert.Equal(4, await db.BodyWeights.CountAsync());
        Assert.Equal(4, await db.SleepSessions.CountAsync());
    }


    [Fact]
    public async Task Interrupted_reconciliation_does_not_delete_data_or_require_persistent_staging()
    {
        await using var db = Context();
        db.TenantId = tenantId;
        db.StepCounts.Add(new StepCountEntity
        {
            Id = Guid.NewGuid(), Timestamp = from.AddHours(6).UtcDateTime, Metric = 42,
            DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "existing"
        });
        await db.SaveChangesAsync();
        await UseTenantAsync(db, tenantId);
        var interrupted = Writer(db);
        var run = await interrupted.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        await interrupted.StageReconciliationIdsAsync(run, "steps", ["new"], default);

        var restarted = Writer(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CompleteReconciliationAsync(run, default));
        Assert.Equal("existing", (await db.StepCounts.AsNoTracking().SingleAsync()).SyncIdentifier);
        var retry = await restarted.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        await restarted.StageReconciliationIdsAsync(retry, "steps", ["existing"], default);
        await restarted.CompleteReconciliationAsync(retry, default);
        Assert.Single(await db.StepCounts.AsNoTracking().ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM pg_class
            WHERE relname LIKE 'google_health_%'
            """).SingleAsync());
    }

    private NocturneDbContext Context() => new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseNpgsql(connectionString).Options);

    private async Task UseTenantAsync(NocturneDbContext db, Guid tenant)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET ROLE nocturne_app");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.current_tenant_id', {tenant.ToString()}, false)");
        db.TenantId = tenant;
    }

    private static GoogleHealthReadingWriter Writer(NocturneDbContext db) => new(
        Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(), Mock.Of<IBodyWeightService>(),
        Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);

}

public sealed class GoogleHealthPostgresFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Database = await SharedPostgres.CreateMigratedDatabaseAsync("google_health");

    public Task DisposeAsync() => Task.CompletedTask;
}
