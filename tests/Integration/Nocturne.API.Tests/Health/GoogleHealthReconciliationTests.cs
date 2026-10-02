using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
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
    public async Task Minute_averages_use_half_open_utc_ranges_and_preserve_raw_tenant_data()
    {
        await using var db = Context();
        db.TenantId = tenantId;
        var start = from.AddSeconds(10).UtcDateTime;
        var end = from.AddMinutes(2).UtcDateTime;
        foreach (var (timestamp, bpm, deleted) in new[]
        {
            (start.AddMilliseconds(-1), 200, false),
            (start, 60, false),
            (from.AddSeconds(30).UtcDateTime, 65, false),
            (from.AddSeconds(40).UtcDateTime, 200, true),
            (from.AddMinutes(1).AddSeconds(10).UtcDateTime, 80, false),
            (end.AddMilliseconds(-1), 90, false),
            (end, 200, false)
        })
            db.HeartRates.Add(new HeartRateEntity
            {
                Id = Guid.NewGuid(), Timestamp = timestamp, Bpm = bpm,
                DeletedAt = deleted ? DateTime.UtcNow : null, DataSource = GoogleHealthReadingWriter.Source
            });
        await db.SaveChangesAsync();
        db.TenantId = otherTenantId;
        db.HeartRates.Add(new HeartRateEntity
        {
            Id = Guid.NewGuid(), Timestamp = start, Bpm = 200, DataSource = GoogleHealthReadingWriter.Source
        });
        await db.SaveChangesAsync();
        await UseTenantAsync(db, tenantId);
        var service = new HeartRateService(db, Mock.Of<IDocumentProcessingService>(),
            Mock.Of<ISignalRBroadcastService>(), NullLogger<HeartRateService>.Instance);

        var averages = (await service.GetHeartRateMinuteAveragesByDateRangeAsync(start, end)).ToArray();

        Assert.Equal([from.UtcDateTime, from.AddMinutes(1).UtcDateTime], averages.Select(row => row.Timestamp));
        Assert.All(averages, row => Assert.Equal(DateTimeKind.Utc, row.Timestamp.Kind));
        Assert.Equal([63, 85], averages.Select(row => row.Bpm));
        Assert.Equal(7, await db.HeartRates.IgnoreQueryFilters().Where(row => row.TenantId == tenantId).CountAsync());
    }

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

    [Fact]
    public async Task Purge_releases_sync_keys_and_preserves_other_sources_and_tenants()
    {
        await using var db = Context();
        foreach (var tenant in new[] { tenantId, otherTenantId })
        {
            db.TenantId = tenant;
            foreach (var identifier in new[] { "active", "user-deleted", "system-deleted", "other-source" })
            {
                var source = identifier == "other-source" ? "manual" : GoogleHealthReadingWriter.Source;
                var deletedAt = identifier.EndsWith("deleted") ? DateTime.UtcNow : (DateTime?)null;
                var heartRate = new HeartRateEntity { Id = Guid.NewGuid(), Timestamp = from.UtcDateTime, Bpm = 60,
                    DataSource = source, SyncIdentifier = identifier, DeletedAt = deletedAt };
                var steps = new StepCountEntity { Id = Guid.NewGuid(), Timestamp = from.UtcDateTime, Metric = 42,
                    DataSource = source, SyncIdentifier = identifier, DeletedAt = deletedAt };
                var weight = new BodyWeightEntity { Id = Guid.NewGuid(), Mills = from.ToUnixTimeMilliseconds(), WeightKg = 70,
                    DataSource = source, SyncIdentifier = identifier, DeletedAt = deletedAt };
                db.HeartRates.Add(heartRate);
                db.StepCounts.Add(steps);
                db.BodyWeights.Add(weight);
                foreach (var entity in new object[] { heartRate, steps, weight })
                    db.Entry(entity).Property("DeletedByUser").CurrentValue = identifier == "user-deleted";
            }
            var session = new SleepSessionEntity { Id = Guid.NewGuid(), StartTime = from.UtcDateTime.AddHours(-8),
                EndTime = from.UtcDateTime, Source = "Google", SourceApp = "Google Health", OriginalId = "sleep-key" };
            db.SleepSessions.Add(session);
            db.SleepStages.Add(new SleepStageEntity { Id = Guid.NewGuid(), SleepSessionId = session.Id,
                StartTime = session.StartTime, EndTime = session.EndTime, Stage = "Light" });
            db.SleepSessions.Add(new SleepSessionEntity { Id = Guid.NewGuid(), StartTime = from.UtcDateTime.AddHours(-8),
                EndTime = from.UtcDateTime, Source = "Google", SourceApp = "other-app", OriginalId = "other-sleep" });
            await db.SaveChangesAsync();
        }
        await UseTenantAsync(db, tenantId);
        await Writer(db).PurgeAsync(default);
        foreach (var count in new[] {
                     await db.HeartRates.IgnoreQueryFilters().Where(row => row.TenantId == tenantId).CountAsync(),
                     await db.StepCounts.IgnoreQueryFilters().Where(row => row.TenantId == tenantId).CountAsync(),
                     await db.BodyWeights.IgnoreQueryFilters().Where(row => row.TenantId == tenantId).CountAsync() })
            Assert.Equal(1, count);
        Assert.Equal("other-app", (await db.SleepSessions.AsNoTracking().SingleAsync()).SourceApp);
        Assert.Empty(await db.SleepStages.AsNoTracking().ToListAsync());

        db.ChangeTracker.Clear();
        db.HeartRates.Add(new HeartRateEntity { Id = Guid.NewGuid(), Timestamp = from.UtcDateTime, Bpm = 70,
            DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "user-deleted" });
        db.StepCounts.Add(new StepCountEntity { Id = Guid.NewGuid(), Timestamp = from.UtcDateTime, Metric = 50,
            DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "user-deleted" });
        db.BodyWeights.Add(new BodyWeightEntity { Id = Guid.NewGuid(), Mills = from.ToUnixTimeMilliseconds(), WeightKg = 75,
            DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "user-deleted" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.HeartRates.CountAsync());
        Assert.Equal(2, await db.StepCounts.CountAsync());
        Assert.Equal(2, await db.BodyWeights.CountAsync());

        await UseTenantAsync(db, otherTenantId);
        Assert.Equal(4, await db.HeartRates.IgnoreQueryFilters().Where(row => row.TenantId == otherTenantId).CountAsync());
        Assert.Equal(4, await db.StepCounts.IgnoreQueryFilters().Where(row => row.TenantId == otherTenantId).CountAsync());
        Assert.Equal(4, await db.BodyWeights.IgnoreQueryFilters().Where(row => row.TenantId == otherTenantId).CountAsync());
        Assert.Equal(2, await db.SleepSessions.CountAsync());
        Assert.Single(await db.SleepStages.ToListAsync());
    }

    [Fact]
    public async Task Replicas_share_progress_and_exclude_concurrent_operations_per_tenant()
    {
        await SeedConnectorAsync();
        await using var firstProvider = ReplicaServices();
        await using var secondProvider = ReplicaServices();
        var first = new GoogleHealthCoordinator(firstProvider.GetRequiredService<IServiceScopeFactory>());
        var second = new GoogleHealthCoordinator(secondProvider.GetRequiredService<IServiceScopeFactory>());
        var lease = await first.AcquireAsync(tenantId, default);
        try
        {
            Assert.Null(await second.AcquireAsync(tenantId, default, TimeSpan.Zero));
            Assert.False(await second.QueueAsync(tenantId, 4, default).WaitAsync(TimeSpan.FromSeconds(5)));
            await using var unrelated = await second.AcquireAsync(otherTenantId, default, TimeSpan.Zero);
            Assert.NotNull(unrelated);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireAsync(tenantId, cancellation.Token));
            await first.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);
            var observed = await second.ProgressAsync(tenantId, default);
            Assert.Equal(GoogleHealthSyncPhase.Reading, observed!.Phase);
            Assert.Equal(3, observed.PagesRead);
            Assert.Null(await second.ProgressAsync(otherTenantId, default));
            Assert.False(await second.QueueAsync(tenantId, 4, default));

            using var scope = firstProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            db.TenantId = tenantId;
            var cursors = new ConnectorSyncCursorStore(db, NullLogger<ConnectorSyncCursorStore>.Instance);
            await cursors.SetAsync("GoogleHealth", "health", new(null, "{\"backfillComplete\":true}"));
            Assert.Equal(3, (await second.ProgressAsync(tenantId, default))!.PagesRead);
            await first.CompleteScheduledAsync(tenantId);
        }
        finally { await lease!.DisposeAsync(); }
        await using var reacquired = await second.AcquireAsync(tenantId, default, TimeSpan.Zero);
        Assert.NotNull(reacquired);
        Assert.Null(await second.ProgressAsync(tenantId, default));
    }

    [Fact]
    public async Task Durable_manual_requests_survive_replica_loss_and_have_one_worker_owner()
    {
        await SeedConnectorAsync();
        await using var provider = ReplicaServices();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var first = new GoogleHealthCoordinator(scopes);
        var second = new GoogleHealthCoordinator(scopes);
        Assert.True(await first.QueueAsync(tenantId, 4, default));
        Assert.False(await second.QueueAsync(tenantId, 4, default));
        var claim = await first.ClaimWorkerAsync(tenantId, default);
        try
        {
            Assert.NotNull(claim);
            Assert.Null(await second.ClaimWorkerAsync(tenantId, default));
            Assert.True(await first.StartQueuedAsync(tenantId, default));
            await first.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, "weight", 2, 4, 5);
            await first.CompleteScheduledAsync(tenantId);
            Assert.Equal(5, (await second.ProgressAsync(tenantId, default))!.PagesRead);
        }
        finally { await claim!.DisposeAsync(); }

        var restarted = new GoogleHealthCoordinator(scopes);
        await using var requests = restarted.ReadRequestsAsync(default).GetAsyncEnumerator();
        Assert.True(await requests.MoveNextAsync());
        Assert.Equal(tenantId, requests.Current);
        await using var newClaim = await restarted.ClaimWorkerAsync(tenantId, default);
        Assert.NotNull(newClaim);
        Assert.True(await restarted.StartQueuedAsync(tenantId, default));
        await restarted.CompleteAsync(tenantId);
        Assert.Null(await second.ProgressAsync(tenantId, default));
        Assert.True(await second.QueueAsync(tenantId, 4, default));
    }

    [Fact]
    public async Task Durable_queue_notifications_dispatch_without_repeated_tenant_sweeps()
    {
        await SeedConnectorAsync();
        using var provider = ReplicaServices();
        var tenants = Mock.Get(provider.GetRequiredService<ITenantService>());
        tenants.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var listener = new GoogleHealthCoordinator(scopes);
        var sender = new GoogleHealthCoordinator(scopes);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var requests = listener.ReadRequestsAsync(cancellation.Token).GetAsyncEnumerator();
        var next = requests.MoveNextAsync().AsTask();
        while (tenants.Invocations.Count == 0) await Task.Delay(10, cancellation.Token);
        Assert.False(next.IsCompleted);

        Assert.True(await sender.QueueAsync(tenantId, 1, cancellation.Token));
        Assert.True(await next.WaitAsync(cancellation.Token));
        Assert.Equal(tenantId, requests.Current);
        await sender.CompleteAsync(tenantId);
        next = requests.MoveNextAsync().AsTask();
        Assert.True(await sender.QueueAsync(tenantId, 1, cancellation.Token));
        Assert.True(await next.WaitAsync(cancellation.Token));
        Assert.Equal(tenantId, requests.Current);
        tenants.Verify(service => service.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        await sender.CompleteAsync(tenantId);
    }

    [Fact]
    public async Task Tenants_with_the_same_legacy_folded_key_have_independent_replica_locks()
    {
        var firstTenant = Guid.Empty;
        var secondTenant = new Guid("00000001-0001-0000-0000-000000000000");
        await using var provider = ReplicaServices();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var first = new GoogleHealthCoordinator(scopes);
        var second = new GoogleHealthCoordinator(scopes);
        await using var operation = await first.AcquireAsync(firstTenant, default);
        await using var unrelated = await second.AcquireAsync(secondTenant, default, TimeSpan.Zero);
        Assert.NotNull(unrelated);
        Assert.Null(await second.AcquireAsync(firstTenant, default, TimeSpan.Zero));
        await using var worker = await second.ClaimWorkerAsync(firstTenant, default);
        Assert.NotNull(worker);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"legacy\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Non_object_cursor_payloads_recover_for_cursor_writes_status_and_queue(string legacyJson)
    {
        await SeedConnectorAsync(legacyJson);
        await using var provider = ReplicaServices();
        var coordinator = new GoogleHealthCoordinator(provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Null(await coordinator.ProgressAsync(tenantId, default));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = tenantId;
        var cursors = new ConnectorSyncCursorStore(db, NullLogger<ConnectorSyncCursorStore>.Instance);
        await cursors.SetAsync("GoogleHealth", "health", new(null, "restored"));
        Assert.Equal("restored", (await cursors.GetAsync("GoogleHealth", "health"))!.LastGuid);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE connector_configurations SET sync_cursors = {legacyJson}::jsonb
            WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
            """);
        Assert.True(await coordinator.QueueAsync(tenantId, 4, default));
        Assert.True((await coordinator.ProgressAsync(tenantId, default))!.WorkerOwned);
        await coordinator.CompleteAsync(tenantId);
        Assert.Null(await coordinator.ProgressAsync(tenantId, default));

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE connector_configurations SET sync_cursors = {legacyJson}::jsonb
            WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
            """);
        await coordinator.CompleteAsync(tenantId);
        db.ChangeTracker.Clear();
        Assert.Equal("{}", await db.ConnectorConfigurations.Select(row => row.SyncCursorsJson).SingleAsync());
    }

    private async Task SeedConnectorAsync(string cursorJson = "{\"unrelated\":{\"LastUpdatedAt\":\"retained\",\"LastGuid\":null}}")
    {
        await using var db = Context();
        db.TenantId = tenantId;
        db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.NewGuid(), ConnectorName = "GoogleHealth", ConfigurationJson = "{}",
            SyncCursorsJson = cursorJson
        });
        await db.SaveChangesAsync();
    }

    private ServiceProvider ReplicaServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDbContext<NocturneDbContext>(options => options
            .UseNpgsql(fixture.Database.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor()));
        var tenants = new Mock<ITenantService>();
        tenants.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            [new TenantDto(tenantId, "synthetic", "Synthetic", true, DateTime.UtcNow)]);
        services.AddSingleton(tenants.Object);
        return services.BuildServiceProvider();
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
