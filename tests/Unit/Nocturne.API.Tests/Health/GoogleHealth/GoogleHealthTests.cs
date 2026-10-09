using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthTests
{
    [Fact]
    public void Dedicated_connection_preserves_data_source_password_without_enabling_security_info()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic-password");
        using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource).Options);
        Assert.Null(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Password);

        using var connection = GoogleHealthCoordinator.CreateDedicatedConnection(db);
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("synthetic-password", settings.Password);
        Assert.False(settings.Pooling);
        Assert.False(settings.PersistSecurityInfo);
        Assert.Equal("synthetic", settings.Username);
        Assert.Equal("synthetic", settings.Database);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"invalid\"")]
    [InlineData("42")]
    [InlineData("{\"Phase\":\"invalid\"}")]
    [InlineData("{\"WorkerOwned\":\"invalid\"}")]
    [InlineData("{\"PagesRead\":{}}")]
    public async Task Invalid_persisted_progress_does_not_block_status(string progress)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "invalid-progress", DisplayName = "Synthetic", IsActive = true });
        await db.SaveChangesAsync();
        db.TenantId = tenantId;
        db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.NewGuid(), ConnectorName = "GoogleHealth", ConfigurationJson = "{}",
            SyncCursorsJson = "{\"googleHealthProgress\":" + progress + "}"
        });
        await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Mock.Of<ITenantAccessor>());
        using var provider = services.BuildServiceProvider();
        var coordinator = new GoogleHealthCoordinator(provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Null(await coordinator.ProgressAsync(tenantId, default));
    }

    [Fact]
    public async Task Writer_uses_existing_native_tables_and_preserves_empty_types_and_other_tenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        db.Tenants.AddRange(
            new TenantEntity { Id = tenantId, Slug = "reconcile-test", DisplayName = "Synthetic", IsActive = true },
            new TenantEntity { Id = otherTenantId, Slug = "reconcile-other", DisplayName = "Other", IsActive = true });
        await db.SaveChangesAsync();
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
        db.TenantId = tenantId;
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);
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
        db.TenantId = otherTenantId;
        Assert.Equal(4, await db.HeartRates.CountAsync());
        Assert.Equal(4, await db.StepCounts.CountAsync());
        Assert.Equal(4, await db.BodyWeights.CountAsync());
        Assert.Equal(4, await db.SleepSessions.CountAsync());
    }

    [Fact]
    public async Task Reconciliation_is_bounded_and_rejects_cross_tenant_runs()
    {
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        db.TenantId = tenantId;
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);
        var run = await writer.BeginReconciliationAsync(["steps"], from, from.AddDays(1), default);
        var identifiers = Enumerable.Range(0, 100_000).Select(index => "id-" + index).ToArray();
        await writer.StageReconciliationIdsAsync(run, "steps", identifiers, default);
        await writer.StageReconciliationIdsAsync(run, "steps", ["id-0", "", " "], default);
        var limit = await Assert.ThrowsAsync<GoogleHealthException>(() =>
            writer.StageReconciliationIdsAsync(run, "steps", ["over-limit"], default));
        Assert.Equal("history_too_large", limit.Message);

        db.TenantId = otherTenantId;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.StageReconciliationIdsAsync(run, "steps", ["cross-tenant"], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteReconciliationAsync(run, default));
        await writer.AbandonReconciliationAsync(run, default);
        db.TenantId = tenantId;
        await writer.AbandonReconciliationAsync(run, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteReconciliationAsync(run, default));
    }

    [Fact]
    public void Google_health_does_not_add_database_migrations()
    {
        using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_discovery", options => options.UseNocturneMigrations()).Options);

        Assert.DoesNotContain(db.Database.GetMigrations(), migration => migration.Contains("GoogleHealth"));
    }

    [Fact]
    public async Task Sleep_reconciliation_uses_end_time_and_preserves_other_sources_and_tenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        db.TenantId = tenantId;
        db.Tenants.AddRange(
            new TenantEntity { Id = tenantId, Slug = "sleep-window", DisplayName = "Sleep window", IsActive = true },
            new TenantEntity { Id = otherTenantId, Slug = "other-sleep", DisplayName = "Other sleep", IsActive = true });
        var from = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        SleepSessionEntity Session(string identifier, DateTimeOffset end, string source = "Google", string app = "Google Health") => new()
        {
            Id = Guid.NewGuid(), OriginalId = identifier, Source = source, SourceApp = app,
            StartTime = end.AddHours(-8).UtcDateTime, EndTime = end.UtcDateTime
        };
        db.SleepSessions.AddRange(
            Session("before", from.AddSeconds(-1)),
            Session("lower-bound", from),
            Session("stale-overnight", from.AddHours(6)),
            Session("retained", from.AddHours(7)),
            Session("upper-bound", to),
            Session("after", to.AddHours(6)),
            Session("other-source", from.AddHours(6), "Manual"),
            Session("other-app", from.AddHours(6), app: "Other app"));
        var userDeletedAt = from.AddDays(-1).UtcDateTime;
        var userDeleted = Session("user-deleted", from.AddHours(6));
        userDeleted.DeletedAt = userDeletedAt;
        db.SleepSessions.Add(userDeleted);
        db.Entry(userDeleted).Property("DeletedByUser").CurrentValue = true;
        await db.SaveChangesAsync();
        db.TenantId = otherTenantId;
        db.SleepSessions.Add(Session("other-tenant", from.AddHours(6)));
        await db.SaveChangesAsync();
        db.TenantId = tenantId;
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);

        var run = await writer.BeginReconciliationAsync(["sleep"], from, to, default);
        await writer.StageReconciliationIdsAsync(run, "sleep", ["retained"], default);
        await writer.CompleteReconciliationAsync(run, default);

        var remaining = await db.SleepSessions.AsNoTracking().Select(session => session.OriginalId).ToListAsync();
        Assert.Equal(6, remaining.Count);
        Assert.DoesNotContain("lower-bound", remaining);
        Assert.DoesNotContain("stale-overnight", remaining);
        Assert.Contains("retained", remaining);
        Assert.Contains("upper-bound", remaining);
        Assert.Contains("after", remaining);
        Assert.Contains("before", remaining);
        Assert.Contains("other-source", remaining);
        Assert.Contains("other-app", remaining);
        var stored = await db.SleepSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(session => session.TenantId == tenantId)
            .Select(session => new { session.OriginalId, session.DeletedAt, DeletedByUser = EF.Property<bool>(session, "DeletedByUser") })
            .ToListAsync();
        Assert.Equal(9, stored.Count);
        var stale = Assert.Single(stored, session => session.OriginalId == "stale-overnight");
        Assert.NotNull(stale.DeletedAt);
        Assert.False(stale.DeletedByUser);
        var deleted = Assert.Single(stored, session => session.OriginalId == "user-deleted");
        Assert.Equal(userDeletedAt, deleted.DeletedAt);
        Assert.True(deleted.DeletedByUser);
        db.TenantId = otherTenantId;
        Assert.Equal("other-tenant", (await db.SleepSessions.AsNoTracking().SingleAsync()).OriginalId);
    }

    [Fact]
    public async Task Reimporting_and_purging_google_health_sleep_preserves_other_google_sources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;
        await using var db = new NocturneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        db.TenantId = tenantId;
        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "sleep-test", DisplayName = "Sleep test", IsActive = true });
        await db.SaveChangesAsync();
        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(value => value.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new NocturneDbContext(options) { TenantId = tenantId }));
        var repository = new SleepSessionRepository(factory.Object);
        var sleepService = new Mock<ISleepService>();
        sleepService.Setup(value => value.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Returns<SleepSession, CancellationToken>(repository.UpsertSessionAsync);
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), sleepService.Object, db, NullLogger<GoogleHealthReadingWriter>.Instance);
        var start = new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc);
        var otherSource = await repository.UpsertSessionAsync(new SleepSession
        {
            Source = SleepSource.Google, OriginalId = "google-sleep-1", SourceApp = "Google Fit",
            StartTime = start, EndTime = start.AddHours(8),
            Stages = [new SleepStageInterval { StartTime = start, EndTime = start.AddHours(1), Stage = SleepStageType.Light }]
        });
        using var document = JsonDocument.Parse("""
            {"dataPointName":"google-sleep-1","sleep":{
              "interval":{"startTime":"2026-09-01T22:00:00Z","endTime":"2026-09-02T06:00:00Z"},
              "stages":[{"startTime":"2026-09-01T22:00:00Z","endTime":"2026-09-01T23:00:00Z","type":"LIGHT"}]
            }}
            """);
        var session = GoogleHealthClient.ParseSleep(document.RootElement);
        await writer.WriteAsync([], [session], 25, default);
        var first = await db.SleepSessions.AsNoTracking().SingleAsync(value => value.SourceApp == "Google Health");
        session.Stages = [new SleepStageInterval { StartTime = start, EndTime = start.AddHours(2), Stage = SleepStageType.Deep }];
        await writer.WriteAsync([], [session], 25, default);
        var second = await db.SleepSessions.Include(value => value.Stages).AsNoTracking()
            .SingleAsync(value => value.SourceApp == "Google Health");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var stage = Assert.Single(second.Stages);
        Assert.Equal("Deep", stage.Stage);
        Assert.Equal(first.Id, stage.SleepSessionId);
        Assert.Equal(tenantId, stage.TenantId);
        Assert.Equal(2, await db.SleepStages.CountAsync());
        Assert.Equal(2, await db.SleepSessions.CountAsync());
        await writer.PurgeAsync(default);
        var retained = await db.SleepSessions.Include(value => value.Stages).AsNoTracking().SingleAsync();
        Assert.Equal(otherSource.Id, retained.Id.ToString());
        Assert.Equal("Google Fit", retained.SourceApp);
        Assert.Equal("google-sleep-1", retained.OriginalId);
        Assert.Equal("Light", Assert.Single(retained.Stages).Stage);
    }

    [Fact]
    public void Sync_phase_uses_the_stable_wire_value() => Assert.Equal(
        "\"refreshing_session\"",
        JsonSerializer.Serialize(GoogleHealthSyncPhase.RefreshingSession));

    [Fact]
    public async Task Queue_tracks_one_manual_import_per_tenant()
    {
        var tenantId = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();

        Assert.True(await coordinator.QueueAsync(tenantId, 4, default));
        Assert.False(await coordinator.QueueAsync(tenantId, 4, default));
        await using var requests = coordinator.ReadRequestsAsync(default).GetAsyncEnumerator();
        Assert.True(await requests.MoveNextAsync());
        Assert.Equal(tenantId, requests.Current);
        await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.RefreshingSession);
        Assert.True(await coordinator.StartQueuedAsync(tenantId, default));
        await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);

        var progress = Assert.IsType<GoogleHealthCoordinator.SyncProgress>(await coordinator.ProgressAsync(tenantId, default));
        Assert.Equal((GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3),
            (progress.Phase, progress.DataType, progress.CompletedDataTypes,
                progress.TotalDataTypes, progress.PagesRead));
        await coordinator.CompleteAsync(tenantId);
        Assert.Null(await coordinator.ProgressAsync(tenantId, default));
    }

    [Fact]
    public async Task Queue_does_not_block_scheduled_progress_and_replaces_only_abandoned_snapshots()
    {
        var coordinator = new GoogleHealthCoordinator();
        var tenant = Guid.NewGuid();
        var operation = await coordinator.AcquireAsync(tenant, default);
        try
        {
            await coordinator.ReportAsync(tenant, GoogleHealthSyncPhase.Reading);
            Assert.False(await coordinator.QueueAsync(tenant, 4, default).WaitAsync(TimeSpan.FromSeconds(5)));
            await coordinator.ReportAsync(tenant, GoogleHealthSyncPhase.Integrating).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GoogleHealthSyncPhase.Integrating, (await coordinator.ProgressAsync(tenant, default))!.Phase);
        }
        finally { await operation!.DisposeAsync(); }
        Assert.True(await coordinator.QueueAsync(tenant, 4, default));
        Assert.True((await coordinator.ProgressAsync(tenant, default))!.WorkerOwned);
    }

    [Theory]
    [InlineData("http://example.com/settings/connectors/google-health/callback")]
    [InlineData("https://192.168.2.238/settings/connectors/google-health/callback")]
    [InlineData("https://example.com/settings/connectors/google-health/callback?x=1")]
    [InlineData("https://example.com/auth/login")]
    public void Requires_exact_https_callback(string url)
    {
        var options = Options();
        options.CallbackUrl = url;
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
    }

    [Fact]
    public void Import_selection_and_history_are_validated()
    {
        var options = Options();
        options.DataTypes = [];
        options.ImportFrom = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        GoogleHealthService.ValidateOptions(options);

        options.DataTypes = ["body-fat"];
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
        options.DataTypes = ["weight"];
        options.ImportFrom = DateTimeOffset.UtcNow.AddDays(2);
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
    }

    [Fact]
    public async Task OAuth_and_options_use_only_connector_storage()
    {
        var tenantId = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var store = new TestConnectorStore();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""),
            "/v1/userinfo" => Json("{\"sub\":\"account\"}"),
            "/revoke" => Json("{}"),
            _ => Json("{}")
        });
        var coordinator = new GoogleHealthCoordinator();
        var service = Service(store, handler, tenantId, coordinator);
        var options = Options();
        options.DataTypes = ["weight", "steps"];

        await service.SaveAsync(options, subject, default);
        Assert.False(store.IsActive);
        var authorization = await service.StartAsync(subject, default);
        var query = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query);
        Assert.Equal("S256", query["code_challenge_method"]);

        await service.CompleteAsync(new GoogleHealthCallback
        {
            State = query["state"].ToString(),
            Code = "code"
        }, subject, default);

        var status = await service.StatusAsync(default);
        Assert.True(status.Configured);
        Assert.True(status.Connected);
        Assert.True(store.IsActive);
        Assert.Equal("partial_consent", status.ErrorCode);
        Assert.Equal("refresh", store.Secrets["refreshToken"]);
        var accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("account")));
        Assert.Equal(accountKey, store.Secrets["accountKey"]);

        await service.DisconnectAsync(subject, default);
        var disconnected = await service.StatusAsync(default);
        Assert.False(disconnected.Connected);
        Assert.True(disconnected.Configured);
        Assert.False(store.IsActive);
        Assert.Equal(accountKey, store.Secrets["accountKey"]);
        var reconnect = await service.StartAsync(subject, default);
        Assert.Contains("client_id=synthetic.apps.googleusercontent.com", reconnect.Url);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"legacy\"")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task Malformed_runtime_cursor_keeps_valid_connection_status(string runtimeState)
    {
        var store = new TestConnectorStore();
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());
        var options = Options();
        await service.SaveAsync(options, Guid.NewGuid(), default);
        var secrets = store.Secrets.ToDictionary(value => value.Key, value => value.Value);
        secrets["refreshToken"] = "refresh";
        secrets["grantedScopes"] = GoogleHealthClient.MetricsScope;
        await store.Configurations.SaveSecretsAsync("GoogleHealth", secrets);
        await store.Cursors.SetAsync("GoogleHealth", "health", new(null, runtimeState));
        var status = await service.StatusAsync(default);
        Assert.True(status.Configured);
        Assert.True(status.Connected);
        Assert.Null(status.ErrorCode);
        Assert.Equal(options.DataTypes, status.SelectedTypes);
        Assert.Null(status.BackfillSyncedThrough);
        Assert.False(status.BackfillComplete);
    }

    [Fact]
    public async Task Status_exposes_persisted_historical_import_progress()
    {
        var store = new TestConnectorStore();
        store.SetConfiguration("""
            {
              "clientId":"synthetic.apps.googleusercontent.com",
              "callbackUrl":"https://example.test:8450/settings/connectors/google-health/callback",
              "syncBodyWeight":true,
              "historyDays":7,
              "backfillCursorDate":"2025-06-01T00:00:00Z",
              "backfillFloorDate":"2020-01-01T00:00:00Z",
              "backfillComplete":false
            }
            """);
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());

        var status = await service.StatusAsync(default);

        Assert.Equal(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), status.BackfillSyncedThrough);
        Assert.False(status.BackfillComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_scope_fallback_matches_the_authorization_request_unless_google_returns_scopes(bool explicitScopes)
    {
        var subject = Guid.NewGuid();
        var store = new TestConnectorStore();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json(explicitScopes
                ? $$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""
                : """{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer"}"""),
            "/v1/userinfo" => Json("{\"sub\":\"account\"}"),
            _ => Json("{}")
        });
        var service = Service(store, handler, Guid.NewGuid());
        var options = Options();
        options.DataTypes = ["weight"];
        await service.SaveAsync(options, subject, default);
        var authorization = await service.StartAsync(subject, default);
        var query = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query);

        await service.CompleteAsync(new GoogleHealthCallback
        {
            State = query["state"].ToString(), Code = "code"
        }, subject, default);

        var expectedScopes = explicitScopes
            ? new[] { "openid", GoogleHealthClient.MetricsScope }
            : query["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedScopes.Order(StringComparer.Ordinal),
            store.Secrets["grantedScopes"].Split(' ').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Changing_import_history_restarts_backfill_without_losing_the_live_watermark()
    {
        var store = new TestConnectorStore();
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());
        var options = Options();
        var subject = Guid.NewGuid();
        await service.SaveAsync(options, subject, default);
        const string resume = """{"lastSyncedTo":"2026-09-01T00:00:00Z","backfillCursorDate":"2020-01-01","backfillFloorDate":"2020-01-01","backfillComplete":true,"backfillChunkDays":7}""";
        await store.Cursors.SetAsync("GoogleHealth", "health", new(null, resume));
        await service.SaveAsync(options, subject, default);
        Assert.Equal(resume, (await store.Cursors.GetAsync("GoogleHealth", "health"))!.LastGuid);
        options.ImportFrom = DateTimeOffset.Parse("2019-01-01T00:00:00Z");
        await service.SaveAsync(options, subject, default);
        using var restarted = JsonDocument.Parse((await store.Cursors.GetAsync("GoogleHealth", "health"))!.LastGuid!);
        Assert.Equal("2026-09-01T00:00:00Z", restarted.RootElement.GetProperty("lastSyncedTo").GetString());
        Assert.False(restarted.RootElement.TryGetProperty("backfillComplete", out _));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"legacy\"")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task Saving_import_settings_repairs_unreadable_runtime_state(string runtimeState)
    {
        var store = new TestConnectorStore();
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());
        var options = Options();
        var subject = Guid.NewGuid();
        await service.SaveAsync(options, subject, default);
        await store.Cursors.SetAsync("GoogleHealth", "health", new(null, runtimeState));
        options.ImportFrom = DateTimeOffset.Parse("2019-01-01T00:00:00Z");
        await service.SaveAsync(options, subject, default);
        Assert.Equal("{}", (await store.Cursors.GetAsync("GoogleHealth", "health"))!.LastGuid);
        Assert.Equal(options.ImportFrom, (await service.StatusAsync(default)).ImportFrom);
    }

    [Fact]
    public async Task OAuth_flow_can_complete_on_a_different_api_replica_and_is_consumed_once()
    {
        var store = new TestConnectorStore();
        var tenant = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json("""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer"}"""),
            "/v1/userinfo" => Json("""{"sub":"account"}"""),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        var first = Service(store, handler, tenant, new GoogleHealthCoordinator());
        await first.SaveAsync(Options(), subject, default);
        var authorization = await first.StartAsync(subject, default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization.Url).Query)["state"]!;
        Assert.True(store.Secrets.ContainsKey("oauthFlow"));
        var second = Service(store, handler, tenant, new GoogleHealthCoordinator());
        var persistedFlow = store.Secrets["oauthFlow"];
        foreach (var (badState, badSubject) in new[] { ("", subject), ("mismatched", subject), (state, Guid.NewGuid()) })
        {
            var rejected = await Assert.ThrowsAsync<GoogleHealthException>(() => second.CompleteAsync(
                new GoogleHealthCallback { State = badState, Code = "code" }, badSubject, default));
            Assert.Equal("expired_signin", rejected.Message);
            Assert.Equal(persistedFlow, store.Secrets["oauthFlow"]);
        }
        var callback = new GoogleHealthCallback { State = state, Code = "code" };
        await second.CompleteAsync(callback, subject, default);
        Assert.False(store.Secrets.ContainsKey("oauthFlow"));
        Assert.True((await second.StatusAsync(default)).Connected);
        var error = await Assert.ThrowsAsync<GoogleHealthException>(() => first.CompleteAsync(callback, subject, default));
        Assert.Equal("expired_signin", error.Message);
    }

    [Fact]
    public async Task Purge_invalidates_pending_oauth_on_other_replicas_and_allows_a_fresh_signin()
    {
        var store = new TestConnectorStore();
        var tenant = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var handler = new StubHandler(_ => throw new InvalidOperationException("No Google request expected"));
        var tokenCache = new ConnectorTokenCache();
        var first = Service(store, handler, tenant, new GoogleHealthCoordinator(), tokenCache);
        await first.SaveAsync(Options(), subject, default);
        var authorization = await first.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();

        await tokenCache.SetAsync("GoogleHealth", tenant, new ConnectorSession("stale-access", DateTime.UtcNow.AddHours(1)));
        var second = Service(store, handler, tenant, new GoogleHealthCoordinator(), tokenCache);
        await second.PurgeAsync(subject, default);

        Assert.False(store.Secrets.ContainsKey("oauthFlow"));
        Assert.Null(await tokenCache.GetAsync("GoogleHealth", tenant));
        var rejected = await Assert.ThrowsAsync<GoogleHealthException>(() => first.CompleteAsync(
            new GoogleHealthCallback { State = state, Code = "code" }, subject, default));
        Assert.Equal("expired_signin", rejected.Message);
        Assert.False((await first.StatusAsync(default)).Connected);
        Assert.False(store.IsActive);
        var fresh = await second.StartAsync(subject, default);
        Assert.NotEqual(state, QueryHelpers.ParseQuery(new Uri(fresh.Url).Query)["state"].ToString());
    }

    [Fact]
    public async Task Purging_a_disconnected_account_removes_all_resume_state_and_preserves_import_settings()
    {
        var store = new TestConnectorStore();
        store.SetConfiguration("""{"enabled":false,"lastSyncedTo":"2026-09-01T00:00:00Z","backfillCursorDate":"2026-01-01","backfillFloorDate":"2020-01-01","backfillComplete":true,"backfillChunkDays":7,"syncIntervalMinutes":30,"importFrom":"2020-01-01T00:00:00Z","dataTypes":["steps"]}""");
        await store.Configurations.SaveSecretsAsync("GoogleHealth", new Dictionary<string, string> { ["accountKey"] = "previous-account" });
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());

        await service.PurgeAsync(Guid.NewGuid(), default);

        foreach (var key in new[] { "lastSyncedTo", "backfillCursorDate", "backfillFloorDate", "backfillComplete", "backfillChunkDays" })
            Assert.False(store.Configuration.TryGetProperty(key, out _));
        Assert.False(store.Secrets.ContainsKey("accountKey"));
        Assert.Null((await store.Cursors.GetAsync("GoogleHealth", "health"))!.LastGuid);
        Assert.Equal("2020-01-01T00:00:00Z", store.Configuration.GetProperty("importFrom").GetString());
        Assert.Equal("steps", store.Configuration.GetProperty("dataTypes")[0].GetString());
        Assert.False(store.Configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, store.Configuration.GetProperty("syncIntervalMinutes").GetInt32());
    }

    [Fact]
    public async Task Setup_without_session_stays_disabled_and_allows_explicit_reconnect()
    {
        var tenantId = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var store = new TestConnectorStore();
        store.SetConfiguration("""{"enabled":false,"syncIntervalMinutes":30,"activeThresholdMinutes":45}""");
        var service = Service(store, new StubHandler(_ => Json("{}")), tenantId);

        await service.SaveAsync(Options(), subject, default);

        Assert.False(store.Configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, store.Configuration.GetProperty("syncIntervalMinutes").GetInt32());
        Assert.Equal(45, store.Configuration.GetProperty("activeThresholdMinutes").GetInt32());
        Assert.Contains("https://accounts.google.com/", (await service.StartAsync(subject, default)).Url);
    }

    [Fact]
    public async Task Saving_import_settings_preserves_operational_health()
    {
        foreach (var error in new[] { "google_unavailable", "internal_sync" })
        {
            var store = new TestConnectorStore();
            var service = Service(store, new StubHandler(_ => throw new InvalidOperationException("No Google request expected")), Guid.NewGuid());
            await service.SaveAsync(Options(), Guid.NewGuid(), default);
            var failedAt = DateTime.UtcNow.AddMinutes(-5);
            var succeededAt = failedAt.AddDays(-1);
            await store.Configurations.UpdateHealthStateAsync("GoogleHealth", lastSyncAttempt: failedAt,
                lastSuccessfulSync: succeededAt, lastErrorMessage: error, lastErrorAt: failedAt, isHealthy: false);
            var options = Options();
            options.DataTypes = ["steps"];

            await service.SaveAsync(options, Guid.NewGuid(), default);

            var health = await store.Configurations.GetConfigurationAsync("GoogleHealth");
            Assert.False(health!.IsHealthy);
            Assert.Equal(error, health.LastErrorMessage);
            Assert.Equal(failedAt, health.LastErrorAt);
            Assert.Equal(failedAt, health.LastSyncAttempt);
            Assert.Equal(succeededAt, health.LastSuccessfulSync);
            Assert.Equal(error, (await service.StatusAsync(default)).ErrorCode);
        }
    }

    [Fact]
    public async Task Preview_reports_each_capability_without_importing()
    {
        var tenantId = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var store = new TestConnectorStore();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}} {{GoogleHealthClient.SleepScope}}"}"""),
            "/v1/userinfo" => Json("{\"sub\":\"account\"}"),
            _ => Json("{\"dataPoints\":[{}]}" )
        });
        var service = Service(store, handler, tenantId);
        await service.SaveAsync(Options(), subject, default);
        var authorization = await service.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();
        await service.CompleteAsync(new GoogleHealthCallback { State = state, Code = "code" }, subject, default);

        var preview = await service.PreviewAsync(subject, default);

        Assert.Equal(GoogleHealthClient.Capabilities.Length, preview.Items.Length);
        Assert.All(preview.Items.Where(item => item.Supported), item =>
        {
            Assert.True(item.Granted);
            Assert.Equal(1, item.Count);
        });
        Assert.All(preview.Items.Where(item => !item.Supported), item =>
        {
            Assert.False(item.Granted);
            Assert.Equal(0, item.Count);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_preserves_refreshed_tokens_after_configuration_invalidates_cache(bool expireCachedToken)
    {
        var tokenCalls = 0;
        var oldTokenCalls = 0;
        var freshTokenCalls = 0;
        var store = new TestConnectorStore();
        var tenantId = Guid.NewGuid();
        var tokenCache = new ConnectorTokenCache();
        store.OnSave = () => tokenCache.Invalidate("GoogleHealth", tenantId);
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                var token = ++tokenCalls == 1 ? "cached" : "refreshed";
                return Json($$"""{"access_token":"{{token}}","refresh_token":"{{token}}-refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}} {{GoogleHealthClient.SleepScope}}"}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"account\"}");
            if (request.Headers.Authorization?.Parameter == "cached")
                return ++oldTokenCalls == 1 ? Json("{\"dataPoints\":[{},{}]}") : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            Assert.Equal("refreshed", request.Headers.Authorization?.Parameter);
            freshTokenCalls++;
            return Json("{\"dataPoints\":[{}]}");
        });
        var service = Service(store, handler, tenantId, tokenCache: tokenCache);
        await ConnectAsync(service);
        if (expireCachedToken) tokenCache.Invalidate("GoogleHealth", tenantId);

        var preview = await service.PreviewAsync(Guid.NewGuid(), default);

        Assert.Equal(2, tokenCalls);
        var supportedCount = GoogleHealthClient.Capabilities.Count(capability => capability.Supported);
        Assert.Equal(expireCachedToken ? 0 : supportedCount, oldTokenCalls);
        Assert.Equal(supportedCount, freshTokenCalls);
        Assert.All(preview.Items.Where(item => item.Supported), item =>
        {
            Assert.Equal(1, item.Count);
            Assert.Null(item.ErrorCode);
        });
        Assert.Equal("refreshed-refresh", store.Secrets["refreshToken"]);
        var status = await service.StatusAsync(default);
        Assert.True(status.Connected);
        Assert.NotNull(status.AccessTokenExpiresAt);
        await service.PreviewAsync(Guid.NewGuid(), default);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(expireCachedToken ? 0 : supportedCount, oldTokenCalls);
    }

    [Fact]
    public async Task Preview_requires_reconnection_only_after_the_refreshed_token_is_rejected()
    {
        var tokenCalls = 0;
        var inventoryCalls = 0;
        var store = new TestConnectorStore();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json($$"""{"access_token":"access-{{++tokenCalls}}","refresh_token":"refresh-{{tokenCalls}}","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}} {{GoogleHealthClient.SleepScope}}"}""");
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"account\"}");
            inventoryCalls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var service = Service(store, handler, Guid.NewGuid());
        await ConnectAsync(service);
        var accountKey = store.Secrets["accountKey"];

        var error = await Assert.ThrowsAsync<GoogleHealthException>(() => service.PreviewAsync(Guid.NewGuid(), default));

        Assert.Equal("reconnect_required", error.Message);
        Assert.Equal(2, tokenCalls);
        var supportedCount = GoogleHealthClient.Capabilities.Count(capability => capability.Supported);
        Assert.Equal(supportedCount * 2, inventoryCalls);
        Assert.False(store.Secrets.ContainsKey("refreshToken"));
        Assert.Equal(accountKey, store.Secrets["accountKey"]);
        var status = await service.StatusAsync(default);
        Assert.False(status.Connected);
        Assert.Equal("reconnect_required", status.ErrorCode);
        Assert.False((await store.Configurations.GetConfigurationAsync("GoogleHealth"))!.IsHealthy);
        await Assert.ThrowsAsync<GoogleHealthException>(() => service.PreviewAsync(Guid.NewGuid(), default));
        Assert.Equal(2, tokenCalls);
        Assert.Equal(supportedCount * 2, inventoryCalls);
    }

    [Fact]
    public async Task Preview_cancellation_during_token_recovery_preserves_the_session_and_releases_the_gate()
    {
        using var cancellation = new CancellationTokenSource();
        var tokenCalls = 0;
        var store = new TestConnectorStore();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                if (++tokenCalls == 2)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"account\"}");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var tenant = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();
        var service = Service(store, handler, tenant, coordinator);
        await ConnectAsync(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewAsync(Guid.NewGuid(), cancellation.Token));

        Assert.Equal(2, tokenCalls);
        Assert.Equal("refresh", store.Secrets["refreshToken"]);
        Assert.True((await service.StatusAsync(default)).Connected);
        Assert.True(await coordinator.Gate(tenant).WaitAsync(0));
        coordinator.Gate(tenant).Release();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission_denied")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "google_unavailable")]
    public async Task Preview_keeps_non_token_failures_per_capability(HttpStatusCode responseStatus, string expectedError)
    {
        var tokenCalls = 0;
        var store = new TestConnectorStore();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenCalls++;
                return Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}} {{GoogleHealthClient.SleepScope}}"}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"account\"}");
            return new HttpResponseMessage(responseStatus);
        });
        var service = Service(store, handler, Guid.NewGuid());
        await ConnectAsync(service);

        var preview = await service.PreviewAsync(Guid.NewGuid(), default);

        Assert.Equal(1, tokenCalls);
        Assert.All(preview.Items.Where(item => item.Supported), item => Assert.Equal(expectedError, item.ErrorCode));
        Assert.Equal("refresh", store.Secrets["refreshToken"]);
        Assert.True((await service.StatusAsync(default)).Connected);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_grant", "reconnect_required", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", "google_unavailable", true)]
    public async Task Preview_refresh_failure_distinguishes_revoked_session_from_temporary_failure(
        HttpStatusCode responseStatus, string providerError, string expectedError, bool connected)
    {
        var tokenCalls = 0;
        var store = new TestConnectorStore();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                if (++tokenCalls == 2)
                    return new HttpResponseMessage(responseStatus)
                    {
                        Content = new StringContent($$"""{"error":"{{providerError}}"}""", Encoding.UTF8, "application/json")
                    };
                return Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"account\"}");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var service = Service(store, handler, Guid.NewGuid());
        await ConnectAsync(service);

        var error = await Assert.ThrowsAsync<GoogleHealthException>(() => service.PreviewAsync(Guid.NewGuid(), default));

        Assert.Equal(expectedError, error.Message);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(connected, (await service.StatusAsync(default)).Connected);
        Assert.Equal(connected, store.Secrets.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task Writer_uses_native_health_tables_without_deleting_on_an_empty_response()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        db.TenantId = tenantId;
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "synthetic",
            DisplayName = "Synthetic",
            IsActive = true
        });
        var timestamp = DateTime.UtcNow.AddHours(-1);
        db.HeartRates.Add(new HeartRateEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            Bpm = 60,
            DataSource = GoogleHealthReadingWriter.Source,
            SyncIdentifier = "old-heart"
        });
        db.BodyWeights.Add(new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(),
            WeightKg = 70,
            DataSource = GoogleHealthReadingWriter.Source,
            SyncIdentifier = "old-weight"
        });
        db.StepCounts.Add(new StepCountEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = timestamp, Metric = 123,
            DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "old-steps"
        });
        db.SleepSessions.Add(new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), StartTime = timestamp.AddHours(-8), EndTime = timestamp,
            Source = "Google", SourceApp = "Google Health", OriginalId = "old-sleep"
        });
        await db.SaveChangesAsync();
        var writer = new GoogleHealthReadingWriter(
            Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db,
            NullLogger<GoogleHealthReadingWriter>.Instance);

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow;
        await writer.WriteAsync([], [], 2, default);
        var run = await writer.BeginReconciliationAsync(["weight", "steps", "heart-rate", "sleep"], from, to, default);
        foreach (var type in new[] { "weight", "steps", "heart-rate", "sleep" })
            await writer.StageReconciliationIdsAsync(run, type, ["", " "], default);
        await writer.CompleteReconciliationAsync(run, default);

        Assert.Single(await db.HeartRates.AsNoTracking().ToListAsync());
        Assert.Single(await db.BodyWeights.AsNoTracking().ToListAsync());
        Assert.Single(await db.StepCounts.AsNoTracking().ToListAsync());
        Assert.Single(await db.SleepSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Writer_skips_manually_deleted_sleep_and_continues_the_import()
    {
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var deleted = new SleepSession { Id = "deleted", EndTime = DateTime.UtcNow };
        var next = new SleepSession { Id = "next", EndTime = deleted.EndTime };
        var sleep = new Mock<ISleepService>();
        sleep.Setup(service => service.UpsertSessionAsync(deleted, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RecreationBlockedException("sleep session", "deleted"));
        sleep.Setup(service => service.UpsertSessionAsync(next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(next);
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), sleep.Object, db, NullLogger<GoogleHealthReadingWriter>.Instance);

        await writer.WriteAsync([], [deleted, next], 2, default);

        sleep.Verify(service => service.UpsertSessionAsync(next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Writer_uses_the_configured_batch_size_for_native_history_writes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var heartRates = new Mock<IHeartRateService>();
        var batches = new List<HeartRate[]>();
        heartRates.Setup(service => service.CreateHeartRatesAsync(
                It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HeartRate>, CancellationToken>((items, _) => batches.Add(items.ToArray()))
            .ReturnsAsync([]);
        var writer = new GoogleHealthReadingWriter(
            heartRates.Object, Mock.Of<IStepCountService>(), Mock.Of<IBodyWeightService>(),
            Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);
        var readings = Enumerable.Range(0, 5).Select(index => new GoogleHealthReading
        {
            DataType = "heart-rate", Mills = index, Value = 60 + index
        }).ToArray();

        await writer.WriteAsync(readings, [], 2, default);

        Assert.Equal([2, 2, 1], batches.Select(batch => batch.Length));
    }

    [Fact]
    public async Task Writer_quarantines_a_single_malformed_reading_without_failing_the_batch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var heartRates = new Mock<IHeartRateService>();
        var batches = new List<HeartRate[]>();
        heartRates.Setup(service => service.CreateHeartRatesAsync(
                It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HeartRate>, CancellationToken>((items, _) => batches.Add(items.ToArray()))
            .ReturnsAsync([]);
        var writer = new GoogleHealthReadingWriter(
            heartRates.Object, Mock.Of<IStepCountService>(), Mock.Of<IBodyWeightService>(),
            Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);
        var readings = new[]
        {
            new GoogleHealthReading { DataType = "heart-rate", Mills = 1, Value = 60 },
            // Out of int range: must be quarantined, not thrown, so the other reading still lands.
            new GoogleHealthReading { DataType = "heart-rate", Mills = 2, Value = (decimal)int.MaxValue + 1 },
            new GoogleHealthReading { DataType = "heart-rate", Mills = 3, Value = 70 },
        };

        await writer.WriteAsync(readings, [], 10, default);

        Assert.Equal(2, Assert.Single(batches).Length);
        Assert.Equal([60, 70], batches.Single().Select(record => record.Bpm));
    }

    [Fact]
    public async Task Sync_endpoint_maps_network_failure_to_problem_details()
    {
        var controller = new GoogleHealthController(new ThrowingGoogleHealthService());

        var result = await controller.SyncGoogleHealth(default);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, response.StatusCode);
        Assert.Equal("google_unavailable", Assert.IsType<ProblemDetails>(response.Value).Detail);
    }

    [Theory]
    [InlineData("rate_limited", 429)]
    [InlineData("google_unavailable", 502)]
    [InlineData("configure_first", 400)]
    public async Task Sync_endpoint_preserves_provider_error_codes_and_retryable_status(string code, int status)
    {
        var controller = new GoogleHealthController(new ThrowingGoogleHealthService(new GoogleHealthException(code)));

        var result = await controller.SyncGoogleHealth(default);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, Assert.IsType<ProblemDetails>(response.Value).Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manual_import_reports_rejection_when_another_import_is_active(bool scheduled)
    {
        var tenant = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();
        var store = new TestConnectorStore();
        var service = Service(store, new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""),
            "/v1/userinfo" => Json("""{"sub":"account"}"""),
            _ => Json("{}")
        }), tenant, coordinator);
        await ConnectAsync(service);
        var options = Options();
        options.PreviewOnly = false;
        await service.SaveAsync(options, Guid.NewGuid(), default);
        await using var operation = scheduled ? await coordinator.AcquireAsync(tenant, default) : null;
        if (!scheduled) await service.QueueSyncAsync(default);

        var error = await Assert.ThrowsAsync<GoogleHealthException>(() => service.QueueSyncAsync(default));

        Assert.Equal("already_running", error.Message);
        Assert.Equal("sync_queue", error.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_and_purge_cancel_queued_work_before_reconnect(bool purge)
    {
        var tenantId = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();
        var store = new TestConnectorStore();
        var service = Service(store, new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""),
            "/v1/userinfo" => Json("""{"sub":"account"}"""),
            _ => Json("{}")
        }), tenantId, coordinator);
        await ConnectAsync(service);
        if (purge) await service.DisconnectAsync(Guid.NewGuid(), default);
        Assert.True(await coordinator.QueueAsync(tenantId, 1, default));
        Assert.True((await service.StatusAsync(default)).IsSyncing);

        if (purge) await service.PurgeAsync(Guid.NewGuid(), default);
        else await service.DisconnectAsync(Guid.NewGuid(), default);

        Assert.False((await service.StatusAsync(default)).IsSyncing);
        Assert.False(await coordinator.StartQueuedAsync(tenantId, default));
        await ConnectAsync(service);
        Assert.True((await service.StatusAsync(default)).Connected);
        Assert.False((await service.StatusAsync(default)).IsSyncing);
        Assert.False(await coordinator.StartQueuedAsync(tenantId, default));
    }

    private static async Task ConnectAsync(GoogleHealthService service)
    {
        var subject = Guid.NewGuid();
        await service.SaveAsync(Options(), subject, default);
        var authorization = await service.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();
        await service.CompleteAsync(new GoogleHealthCallback { State = state, Code = "code" }, subject, default);
    }

    [Fact]
    public async Task Disconnect_interrupts_preview_and_survives_browser_cancellation_before_google_revocation()
    {
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRevoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async (request, ct) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/token": return Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"openid {{GoogleHealthClient.MetricsScope}}"}""");
                case "/v1/userinfo": return Json("""{"sub":"account"}""");
                case "/revoke":
                    revoking.TrySetResult();
                    await releaseRevoke.Task.WaitAsync(ct);
                    return Json("{}");
                default:
                    scanning.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { scanCancelled.TrySetResult(); throw; }
                    return Json("{}");
            }
        });
        var store = new TestConnectorStore();
        var coordinator = new GoogleHealthCoordinator();
        var tenant = Guid.NewGuid();
        var service = Service(store, handler, tenant, coordinator);
        await ConnectAsync(service);
        var preview = service.PreviewAsync(Guid.NewGuid(), default);
        await scanning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var browser = new CancellationTokenSource();
        var disconnect = service.DisconnectAsync(Guid.NewGuid(), browser.Token);
        browser.Cancel();
        var recoveredDisconnect = service.DisconnectAsync(Guid.NewGuid(), default);
        var requestId = (await coordinator.DisconnectRequestAsync(tenant, default))!.RequestId;
        try
        {
            await revoking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scanCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview);
            var status = await service.StatusAsync(default);
            Assert.False(status.Connected);
            Assert.True(status.IsDisconnecting);
            Assert.False(store.IsActive);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewAsync(Guid.NewGuid(), default));
        }
        finally { releaseRevoke.TrySetResult(); }
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        await recoveredDisconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await service.StatusAsync(default)).IsDisconnecting);
        Assert.False(store.Secrets.ContainsKey("refreshToken"));
        Assert.True(store.Secrets.ContainsKey("accountKey"));
        await ConnectAsync(service);
        await service.ResumeDisconnectAsync(Guid.NewGuid(), requestId);
        Assert.True((await service.StatusAsync(default)).Connected);
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }

    private static GoogleHealthService Service(
        TestConnectorStore store,
        HttpMessageHandler handler,
        Guid tenantId,
        GoogleHealthCoordinator? coordinator = null,
        ConnectorTokenCache? tokenCache = null)
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(value => value.IsResolved).Returns(true);
        tenant.SetupGet(value => value.TenantId).Returns(tenantId);
        return new GoogleHealthService(
            coordinator ?? new GoogleHealthCoordinator(),
            new GoogleHealthClient(new HttpClient(handler, false)),
            new GoogleHealthAuthTokenProvider(
                new HttpClient(handler, false),
                tokenCache ?? new ConnectorTokenCache(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                tenant.Object,
                NullLogger<GoogleHealthAuthTokenProvider>.Instance),
            store.Configurations,
            store.Loader,
            tenant.Object,
            store.Cursors);
    }

    private static GoogleHealthOptions Options() => new()
    {
        ClientId = "synthetic.apps.googleusercontent.com",
        ClientSecret = "synthetic-secret",
        CallbackUrl = "https://example.test:8450/settings/connectors/google-health/callback",
        DataTypes = ["weight"]
    };

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class TestConnectorStore
    {
        private JsonDocument? configuration;
        private Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase);
        private ConnectorConfigurationResponse? response;
        public bool? IsActive => response?.IsActive;
        public Action? OnSave { get; set; }

        private ConnectorSyncCursor? runtimeCursor;
        public IConnectorSyncCursorStore Cursors { get; }

        public TestConnectorStore()
        {
            var cursors = new Mock<IConnectorSyncCursorStore>();
            cursors.Setup(store => store.GetAsync("GoogleHealth", "health", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => runtimeCursor);
            cursors.Setup(store => store.SetAsync("GoogleHealth", "health", It.IsAny<ConnectorSyncCursor>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, ConnectorSyncCursor, CancellationToken>((_, _, value, _) => runtimeCursor = value)
                .Returns(Task.CompletedTask);
            Cursors = cursors.Object;
            var configurations = new Mock<IConnectorConfigurationService>();
            configurations.Setup(value => value.GetConfigurationAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => response);
            configurations.Setup(value => value.SaveConfigurationAsync(
                    "GoogleHealth", It.IsAny<JsonDocument>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, JsonDocument, string?, CancellationToken>((_, document, _, _) =>
                {
                    configuration?.Dispose();
                    configuration = JsonDocument.Parse(document.RootElement.GetRawText());
                    response ??= new ConnectorConfigurationResponse
                    {
                        ConnectorName = "GoogleHealth",
                        IsActive = true
                    };
                    response.Configuration = configuration;
                    response.IsActive = !configuration.RootElement.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean();
                    OnSave?.Invoke();
                })
                .ReturnsAsync(() => response!);
            configurations.Setup(value => value.GetSecretsAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase));
            configurations.Setup(value => value.SaveSecretsAsync(
                    "GoogleHealth", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, string?, CancellationToken>((_, values, _, _) =>
                {
                    secrets = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                    OnSave?.Invoke();
                })
                .Returns(Task.CompletedTask);
            configurations.Setup(value => value.UpdateHealthStateAsync(
                    "GoogleHealth", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                    It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, DateTime?, DateTime?, string?, DateTime?, bool?, CancellationToken>(
                    (_, attempt, success, error, errorAt, healthy, _) =>
                    {
                        if (response is null) return;
                        if (attempt is not null) response.LastSyncAttempt = attempt;
                        if (success is not null) response.LastSuccessfulSync = success;
                        if (error is not null) response.LastErrorMessage = error.Length == 0 ? null : error;
                        if (errorAt is not null) response.LastErrorAt = errorAt == DateTime.MinValue ? null : errorAt;
                        if (healthy is not null) response.IsHealthy = healthy.Value;
                    })
                .Returns(Task.CompletedTask);
            Configurations = configurations.Object;

            var loader = new Mock<IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>>();
            loader.Setup(value => value.LoadForTenantAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                var loaded = new GoogleHealthConnectorConfiguration { Enabled = configuration is not null };
                if (configuration is not null)
                    ConnectorConfigurationBinder.ApplyJsonToConfig(configuration, loaded);
                ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, loaded);
                return Task.FromResult(loaded);
            });
            Loader = loader.Object;
        }

        public IConnectorConfigurationService Configurations { get; }
        public IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> Loader { get; }
        public IReadOnlyDictionary<string, string> Secrets => secrets;
        public JsonElement Configuration => configuration!.RootElement;

        public void SetConfiguration(string value)
        {
            configuration?.Dispose();
            configuration = JsonDocument.Parse(value);
            runtimeCursor = new(null, value);
            response = new ConnectorConfigurationResponse
            {
                ConnectorName = "GoogleHealth",
                Configuration = configuration,
                IsActive = !configuration.RootElement.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()
            };
        }
    }

    private sealed class ThrowingGoogleHealthService(Exception? failure = null) : IGoogleHealthService
    {
        public Task<GoogleHealthStatus> StatusAsync(CancellationToken ct) =>
            Task.FromResult(new GoogleHealthStatus());
        public Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<GoogleHealthAuthorize> StartAsync(Guid subject, CancellationToken ct) =>
            Task.FromResult(new GoogleHealthAuthorize());
        public Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DisconnectAsync(Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeDisconnectAsync(Guid subject, Guid requestId) => Task.CompletedTask;
        public Task PurgeAsync(Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct) =>
            Task.FromResult(new GoogleHealthPreview());
        public Task QueueSyncAsync(CancellationToken ct) =>
            throw failure ?? new HttpRequestException("synthetic");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
