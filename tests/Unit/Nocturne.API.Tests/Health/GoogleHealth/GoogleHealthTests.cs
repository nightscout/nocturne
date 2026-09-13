using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthTests
{
    [Fact]
    public void Staging_migration_is_discovered_by_the_runtime_context()
    {
        using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_discovery").Options);

        Assert.Contains("20260913000000_AddGoogleHealthReconciliationStaging", db.Database.GetMigrations());
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
        await db.SaveChangesAsync();
        db.TenantId = otherTenantId;
        db.SleepSessions.Add(Session("other-tenant", from.AddHours(6)));
        await db.SaveChangesAsync();
        db.TenantId = tenantId;
        var writer = new GoogleHealthReadingWriter(Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db, NullLogger<GoogleHealthReadingWriter>.Instance);

        await CreateStagingTablesAsync(db);
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
        db.TenantId = otherTenantId;
        Assert.Equal("other-tenant", (await db.SleepSessions.AsNoTracking().SingleAsync()).OriginalId);
    }

    [Fact]
    public async Task Reimporting_google_sleep_preserves_relational_keys_and_replaces_stages()
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
        var session = new SleepSession
        {
            Source = SleepSource.Google, OriginalId = "google-sleep-1", SourceApp = "Google Health",
            StartTime = start, EndTime = start.AddHours(8),
            Stages = [new SleepStageInterval { StartTime = start, EndTime = start.AddHours(1), Stage = SleepStageType.Light }]
        };
        await writer.WriteAsync([], [session], 25, default);
        var first = await db.SleepSessions.AsNoTracking().SingleAsync();
        session.Stages = [new SleepStageInterval { StartTime = start, EndTime = start.AddHours(2), Stage = SleepStageType.Deep }];
        await writer.WriteAsync([], [session], 25, default);
        var second = await db.SleepSessions.Include(value => value.Stages).AsNoTracking().SingleAsync();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var stage = Assert.Single(second.Stages);
        Assert.Equal("Deep", stage.Stage);
        Assert.Equal(first.Id, stage.SleepSessionId);
        Assert.Equal(tenantId, stage.TenantId);
        Assert.Equal(1, await db.SleepStages.CountAsync());
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

        Assert.True(coordinator.Queue(tenantId, 4));
        Assert.False(coordinator.Queue(tenantId, 4));
        await using var requests = coordinator.ReadRequestsAsync(default).GetAsyncEnumerator();
        Assert.True(await requests.MoveNextAsync());
        Assert.Equal(tenantId, requests.Current);
        coordinator.Report(tenantId, GoogleHealthSyncPhase.RefreshingSession);
        Assert.True(coordinator.StartQueued(tenantId));
        coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);

        var progress = Assert.IsType<GoogleHealthCoordinator.SyncProgress>(coordinator.Progress(tenantId));
        Assert.Equal((GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3),
            (progress.Phase, progress.DataType, progress.CompletedDataTypes,
                progress.TotalDataTypes, progress.PagesRead));
        coordinator.Complete(tenantId);
        Assert.Null(coordinator.Progress(tenantId));
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
        Assert.Equal("partial_consent", status.ErrorCode);
        Assert.Equal("refresh", store.Secrets["refreshToken"]);
        var accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("account")));
        Assert.Equal(accountKey, store.Secrets["accountKey"]);

        await service.DisconnectAsync(subject, default);
        Assert.False((await service.StatusAsync(default)).Connected);
        Assert.Equal(accountKey, store.Secrets["accountKey"]);
    }

    [Fact]
    public async Task Purging_a_disconnected_account_removes_its_resume_watermark()
    {
        var store = new TestConnectorStore();
        store.SetConfiguration("""{"enabled":false,"lastSyncedTo":"2026-09-01T00:00:00Z","syncIntervalMinutes":30}""");
        var service = Service(store, new StubHandler(_ => Json("{}")), Guid.NewGuid());

        await service.PurgeAsync(Guid.NewGuid(), default);

        Assert.False(store.Configuration.TryGetProperty("lastSyncedTo", out _));
        Assert.False(store.Configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, store.Configuration.GetProperty("syncIntervalMinutes").GetInt32());
    }

    [Fact]
    public async Task Saving_options_preserves_unrelated_connector_values_and_enabled_state()
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

    [Fact]
    public async Task Preview_refreshes_a_rejected_token_once_and_restarts_inventory()
    {
        var tokenCalls = 0;
        var oldTokenCalls = 0;
        var freshTokenCalls = 0;
        var store = new TestConnectorStore();
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
        var service = Service(store, handler, Guid.NewGuid());
        await ConnectAsync(service);

        var preview = await service.PreviewAsync(Guid.NewGuid(), default);

        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, oldTokenCalls);
        Assert.Equal(GoogleHealthClient.Capabilities.Count(capability => capability.Supported), freshTokenCalls);
        Assert.All(preview.Items.Where(item => item.Supported), item =>
        {
            Assert.Equal(1, item.Count);
            Assert.Null(item.ErrorCode);
        });
        Assert.Equal("refreshed-refresh", store.Secrets["refreshToken"]);
        Assert.True((await service.StatusAsync(default)).Connected);
        await service.PreviewAsync(Guid.NewGuid(), default);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, oldTokenCalls);
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
        Assert.Equal(2, inventoryCalls);
        Assert.False(store.Secrets.ContainsKey("refreshToken"));
        Assert.Equal(accountKey, store.Secrets["accountKey"]);
        var status = await service.StatusAsync(default);
        Assert.False(status.Connected);
        Assert.Equal("reconnect_required", status.ErrorCode);
        Assert.False((await store.Configurations.GetConfigurationAsync("GoogleHealth"))!.IsHealthy);
        await Assert.ThrowsAsync<GoogleHealthException>(() => service.PreviewAsync(Guid.NewGuid(), default));
        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, inventoryCalls);
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
        await CreateStagingTablesAsync(db);
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

    private static async Task ConnectAsync(GoogleHealthService service)
    {
        var subject = Guid.NewGuid();
        await service.SaveAsync(Options(), subject, default);
        var authorization = await service.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();
        await service.CompleteAsync(new GoogleHealthCallback { State = state, Code = "code" }, subject, default);
    }

    private static Task CreateStagingTablesAsync(NocturneDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE google_health_reconciliation_runs (
            id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, from_time TEXT NOT NULL,
            to_time TEXT NOT NULL, active_types TEXT NOT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE google_health_reconciliation_ids (
            run_id TEXT NOT NULL REFERENCES google_health_reconciliation_runs(id) ON DELETE CASCADE,
            tenant_id TEXT NOT NULL, data_type TEXT NOT NULL, identifier TEXT NOT NULL,
            PRIMARY KEY(run_id, data_type, identifier));
        """);

    private static GoogleHealthService Service(
        TestConnectorStore store,
        HttpMessageHandler handler,
        Guid tenantId,
        GoogleHealthCoordinator? coordinator = null)
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(value => value.IsResolved).Returns(true);
        tenant.SetupGet(value => value.TenantId).Returns(tenantId);
        return new GoogleHealthService(
            coordinator ?? new GoogleHealthCoordinator(),
            new GoogleHealthClient(new HttpClient(handler, false)),
            new GoogleHealthAuthTokenProvider(
                new HttpClient(handler, false),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                tenant.Object,
                NullLogger<GoogleHealthAuthTokenProvider>.Instance),
            store.Configurations,
            store.Loader,
            tenant.Object);
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

        public TestConnectorStore()
        {
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
                })
                .ReturnsAsync(() => response!);
            configurations.Setup(value => value.GetSecretsAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase));
            configurations.Setup(value => value.SaveSecretsAsync(
                    "GoogleHealth", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, string?, CancellationToken>((_, values, _, _) =>
                    secrets = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase))
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
            response = new ConnectorConfigurationResponse
            {
                ConnectorName = "GoogleHealth",
                Configuration = configuration,
                IsActive = true
            };
        }
    }

    private sealed class ThrowingGoogleHealthService : IGoogleHealthService
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
        public Task PurgeAsync(Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct) =>
            Task.FromResult(new GoogleHealthPreview());
        public Task QueueSyncAsync(CancellationToken ct) =>
            throw new HttpRequestException("synthetic");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
