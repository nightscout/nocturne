using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthTests
{
    [Fact]
    public void Maps_google_sleep_session_and_stages_to_nocturne_sleep()
    {
        using var doc = JsonDocument.Parse("""
        {
          "name":"users/example/dataTypes/sleep/dataPoints/night-1",
          "sleep":{
            "interval":{"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T06:00:00Z"},
            "type":"STAGES",
            "stages":[
              {"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T00:00:00Z","type":"LIGHT"},
              {"startTime":"2026-09-05T00:00:00Z","endTime":"2026-09-05T02:00:00Z","type":"DEEP"},
              {"startTime":"2026-09-05T02:00:00Z","endTime":"2026-09-05T04:00:00Z","type":"REM"},
              {"startTime":"2026-09-05T04:00:00Z","endTime":"2026-09-05T06:00:00Z","type":"AWAKE"}
            ],
            "metadata":{"processed":true,"nap":false,"manuallyEdited":false},
            "summary":{"minutesAsleep":"360","minutesAwake":"120","minutesToFallAsleep":"15"}
          }
        }
        """);

        var session = GoogleHealthClient.ParseSleep(doc.RootElement);

        Assert.Equal(SleepSource.Google, session.Source);
        Assert.Equal(SleepSessionType.Overnight, session.Type);
        Assert.Equal(SleepDetectionMethod.AutoFinal, session.DetectionMethod);
        Assert.Equal(8 * 60 * 60 * 1000, session.DurationMs);
        Assert.Equal(6 * 60 * 60 * 1000, session.TotalSleepMs);
        Assert.Equal(2 * 60 * 60 * 1000, session.TotalAwakeMs);
        Assert.Equal(4, session.Stages!.Count);
        Assert.Equal(SleepStageType.Deep, session.Stages[1].Stage);
        Assert.EndsWith("night-1", session.OriginalId);
    }

    [Fact]
    public async Task Writes_google_measurements_to_native_nocturne_health_services()
    {
        var heartRateService = new Mock<IHeartRateService>();
        var stepCountService = new Mock<IStepCountService>();
        var bodyWeightService = new Mock<IBodyWeightService>();
        var sleepService = new Mock<ISleepService>();
        HeartRate[] heartRates = [];
        StepCount[] stepCounts = [];
        BodyWeight[] bodyWeights = [];
        SleepSession? sleepSession = null;
        heartRateService.Setup(service => service.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HeartRate>, CancellationToken>((items, _) => heartRates = items.ToArray())
            .ReturnsAsync((IEnumerable<HeartRate> items, CancellationToken _) => items);
        stepCountService.Setup(service => service.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<StepCount>, CancellationToken>((items, _) => stepCounts = items.ToArray())
            .ReturnsAsync((IEnumerable<StepCount> items, CancellationToken _) => items);
        bodyWeightService.Setup(service => service.CreateBodyWeightsAsync(It.IsAny<IEnumerable<BodyWeight>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<BodyWeight>, CancellationToken>((items, _) => bodyWeights = items.ToArray())
            .ReturnsAsync((IEnumerable<BodyWeight> items, CancellationToken _) => items);
        sleepService.Setup(service => service.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Callback<SleepSession, CancellationToken>((item, _) => sleepSession = item)
            .ReturnsAsync((SleepSession item, CancellationToken _) => item);
        var writer = new GoogleHealthReadingWriter(
            heartRateService.Object, stepCountService.Object, bodyWeightService.Object, sleepService.Object);
        var readings = new[]
        {
            new GoogleHealthReading { DataType = "heart-rate", Mills = 1_000, Value = 65, Unit = "bpm" },
            new GoogleHealthReading { DataType = "steps", Mills = 2_000, EndMills = 3_000, Value = 42, Unit = "steps" },
            new GoogleHealthReading { DataType = "weight", Mills = 4_000, Value = 72.5m, Unit = "kg" }
        };
        var sleep = new SleepSession { OriginalId = "night-1", Source = SleepSource.Google };

        await writer.WriteAsync(
            readings,
            [sleep],
            ["heart-rate", "steps", "weight", "sleep"],
            DateTimeOffset.FromUnixTimeMilliseconds(0),
            DateTimeOffset.FromUnixTimeMilliseconds(10_000),
            default);

        Assert.Equal(65, Assert.Single(heartRates).Bpm);
        Assert.Equal(42, Assert.Single(stepCounts).Metric);
        Assert.Equal(72.5m, Assert.Single(bodyWeights).WeightKg);
        Assert.Equal(GoogleHealthReadingWriter.Source, heartRates[0].DataSource);
        Assert.False(string.IsNullOrWhiteSpace(heartRates[0].SyncIdentifier));
        Assert.Same(sleep, sleepSession);
    }

    [Fact]
    public async Task Reconciliation_preserves_google_types_that_are_not_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant = Guid.NewGuid(); db.TenantId = tenant;
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = "synthetic", DisplayName = "Synthetic", IsActive = true });
        var timestamp = DateTime.UtcNow.AddHours(-1);
        db.HeartRates.Add(new HeartRateEntity { Id = Guid.CreateVersion7(), Timestamp = timestamp, Bpm = 60, DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "old-heart" });
        db.BodyWeights.Add(new BodyWeightEntity { Id = Guid.CreateVersion7(), Mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(), WeightKg = 70, DataSource = GoogleHealthReadingWriter.Source, SyncIdentifier = "old-weight" });
        await db.SaveChangesAsync();
        var writer = new GoogleHealthReadingWriter(
            Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(), Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db);

        await writer.WriteAsync([], [], ["weight"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default);

        Assert.Single(await db.HeartRates.AsNoTracking().ToListAsync());
        Assert.Empty(await db.BodyWeights.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("weight", "{\"weight\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\",\"utcOffset\":\"7200s\"},\"weightGrams\":72500}}", "kg", 72.5)]
    [InlineData("heart-rate", "{\"heartRate\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\"},\"beatsPerMinute\":\"67\"}}", "bpm", 67)]
    [InlineData("steps", "{\"steps\":{\"interval\":{\"startTime\":\"2026-09-01T12:00:00+02:00\",\"endTime\":\"2026-09-01T12:01:00+02:00\"},\"count\":\"42\"}}", "steps", 42)]
    public void Maps_documented_google_types_without_inventing_values(string type, string json, string unit, decimal expected)
    {
        using var doc = JsonDocument.Parse(json);
        var reading = GoogleHealthClient.Parse(type, doc.RootElement);
        Assert.Equal(expected, reading.Value); Assert.Equal(unit, reading.Unit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z").ToUnixTimeMilliseconds(), reading.Mills);
        if (type == "weight") Assert.Equal(120, reading.UtcOffsetMinutes);
        else Assert.Null(reading.UtcOffsetMinutes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"heartRate\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\"}}}")]
    [InlineData("{\"heartRate\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\"},\"beatsPerMinute\":\"-1\"}}")]
    [InlineData("{\"heartRate\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\"},\"beatsPerMinute\":\"60.5\"}}")]
    public void Rejects_missing_or_invalid_samples(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<GoogleHealthException>(() => GoogleHealthClient.Parse("heart-rate", doc.RootElement));
    }

    [Theory]
    [InlineData("http://example.com/settings/connectors/google-health/callback")]
    [InlineData("https://192.168.2.238/settings/connectors/google-health/callback")]
    [InlineData("https://example.com/settings/connectors/google-health/callback?x=1")]
    [InlineData("https://user@example.com/settings/connectors/google-health/callback")]
    [InlineData("https://example.com/auth/login")]
    public void Requires_exact_https_callback(string url)
    {
        var options = Options(); options.CallbackUrl = url;
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
    }

    [Fact]
    public async Task Follows_pages_and_rejects_repeated_page_tokens()
    {
        var calls = 0;
        var requests = new List<Uri>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(++calls == 1 ? "{\"nextPageToken\":\"second\"}" : "{\"dataPoints\":[]}");
        });
        var client = new GoogleHealthClient(new HttpClient(handler));
        Assert.Empty(await client.ReadAsync("synthetic-token", "weight", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default));
        Assert.Equal(2, calls);
        Assert.DoesNotContain("pageToken", requests[0].Query);
        Assert.Equal("second", QueryHelpers.ParseQuery(requests[1].Query)["pageToken"]);
        handler.Responder = _ => Json("{\"nextPageToken\":\"repeated\"}");
        await Assert.ThrowsAsync<GoogleHealthException>(() => client.ReadAsync("synthetic-token", "weight", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default));
    }

    [Theory]
    [InlineData("inventory", "weight", "10000")]
    [InlineData("inventory", "sleep", "25")]
    [InlineData("read", "weight", "10000")]
    [InlineData("sleep", "sleep", "25")]
    public async Task Reads_history_beyond_one_hundred_pages_without_changing_the_time_window(
        string operation, string type, string pageSize)
    {
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = from.AddDays(1);
        var field = type == "sleep" ? "sleep.interval.start_time" : "weight.sample_time.physical_time";
        var expectedFilter = $"{field} >= \"{from.UtcDateTime:O}\" AND {field} < \"{to.UtcDateTime:O}\"";
        var calls = 0;
        var sample = type == "sleep"
            ? """{"name":"users/example/dataTypes/sleep/dataPoints/last-night","sleep":{"interval":{"startTime":"2026-09-01T00:00:00Z","endTime":"2026-09-01T08:00:00Z"}}}"""
            : """{"weight":{"sampleTime":{"physicalTime":"2026-09-01T00:00:00Z"},"weightGrams":72500}}""";
        using var http = new HttpClient(new StubHandler(request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal(expectedFilter, query["filter"].ToString());
            Assert.Equal(pageSize, query["pageSize"].ToString());
            Assert.Equal(calls == 0 ? "" : $"page-{calls + 1}",
                query.TryGetValue("pageToken", out var pageToken) ? pageToken.ToString() : "");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            calls++;
            return Json(calls < 101
                ? JsonSerializer.Serialize(new { dataPoints = Array.Empty<object>(), nextPageToken = $"page-{calls + 1}" })
                : "{\"dataPoints\":[" + sample + "]}");
        }));
        var client = new GoogleHealthClient(http);

        switch (operation)
        {
            case "inventory":
                Assert.Equal(1, await client.CountAsync("synthetic-token", type, from, to, default));
                break;
            case "read":
                var reading = Assert.Single(await client.ReadAsync("synthetic-token", type, from, to, default));
                Assert.Equal(72.5m, reading.Value);
                Assert.Equal(from.ToUnixTimeMilliseconds(), reading.Mills);
                break;
            case "sleep":
                var session = Assert.Single(await client.ReadSleepAsync("synthetic-token", from, to, default));
                Assert.EndsWith("last-night", session.OriginalId);
                Assert.Equal(from.ToUnixTimeMilliseconds(), session.StartMills);
                break;
        }
        Assert.Equal(101, calls);
    }

    [Theory]
    [InlineData("inventory", "inventory", "weight", false)]
    [InlineData("read", "data_read", "weight", false)]
    [InlineData("sleep", "data_read", "sleep", false)]
    [InlineData("inventory", "inventory", "weight", true)]
    [InlineData("read", "data_read", "weight", true)]
    [InlineData("sleep", "data_read", "sleep", true)]
    public async Task Limits_unique_page_tokens_but_allows_completion_on_the_last_page(
        string operation, string stage, string type, bool completesOnLastPage)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            Assert.True(calls <= GoogleHealthClient.MaximumHistoryPages);
            return Json(completesOnLastPage && calls == GoogleHealthClient.MaximumHistoryPages
                ? """{"dataPoints":[]}"""
                : JsonSerializer.Serialize(new { dataPoints = Array.Empty<object>(), nextPageToken = $"page-{calls + 1}" }));
        }));
        var client = new GoogleHealthClient(http);

        if (completesOnLastPage)
        {
            await ReadHistoryAsync(client, operation, default);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => ReadHistoryAsync(client, operation, default));
            Assert.Equal("history_too_large", exception.Message);
            Assert.Equal(stage, exception.Stage);
            Assert.Equal(type, exception.DataType);
        }
        Assert.Equal(GoogleHealthClient.MaximumHistoryPages, calls);
    }

    [Theory]
    [InlineData("inventory", "inventory", "weight")]
    [InlineData("read", "data_read", "weight")]
    [InlineData("sleep", "data_read", "sleep")]
    public async Task Rejects_pagination_cycles_for_each_history_operation(string operation, string stage, string type)
    {
        var calls = 0;
        string[] tokens = ["first", "second", "first"];
        using var http = new HttpClient(new StubHandler(_ =>
            Json(JsonSerializer.Serialize(new { nextPageToken = tokens[calls++] }))));
        var client = new GoogleHealthClient(http);

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => ReadHistoryAsync(client, operation, default));

        Assert.Equal("pagination_failed", exception.Message);
        Assert.Equal(stage, exception.Stage);
        Assert.Equal(type, exception.DataType);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Reports_each_completed_import_page()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Json(calls == 1
                ? """{"dataPoints":[],"nextPageToken":"second"}"""
                : """{"dataPoints":[]}""");
        }));
        var pages = new List<int>();
        var client = new GoogleHealthClient(http);

        await client.ReadAsync("synthetic-token", "weight", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow, default, pages.Add);

        Assert.Equal([1, 2], pages);
    }

    [Fact]
    public async Task Queues_only_one_manual_import_per_tenant_and_tracks_progress()
    {
        var tenant = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();

        Assert.True(coordinator.Queue(tenant, 4));
        Assert.False(coordinator.Queue(tenant, 4));
        await using var requests = coordinator.ReadRequestsAsync(default).GetAsyncEnumerator();
        Assert.True(await requests.MoveNextAsync());
        Assert.Equal(tenant, requests.Current);
        Assert.True(coordinator.StartQueued(tenant));
        coordinator.Report(tenant, "reading", "steps", 1, 4, 3);

        var progress = Assert.IsType<GoogleHealthCoordinator.SyncProgress>(coordinator.Progress(tenant));
        Assert.Equal("reading", progress.Phase);
        Assert.Equal("steps", progress.DataType);
        Assert.Equal(1, progress.CompletedDataTypes);
        Assert.Equal(4, progress.TotalDataTypes);
        Assert.Equal(3, progress.PagesRead);

        coordinator.Complete(tenant);
        Assert.Null(coordinator.Progress(tenant));
    }

    [Theory]
    [InlineData("inventory", true)]
    [InlineData("read", true)]
    [InlineData("sleep", true)]
    [InlineData("inventory", false)]
    [InlineData("read", false)]
    [InlineData("sleep", false)]
    public async Task Stops_history_requests_when_cancelled(string operation, bool cancelBeforeFirstPage)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            if (calls == 3) cancellation.Cancel();
            return Json(JsonSerializer.Serialize(new { dataPoints = Array.Empty<object>(), nextPageToken = $"page-{calls + 1}" }));
        }));
        var client = new GoogleHealthClient(http);
        if (cancelBeforeFirstPage) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadHistoryAsync(client, operation, cancellation.Token));

        Assert.Equal(cancelBeforeFirstPage ? 0 : 3, calls);
    }

    [Theory]
    [InlineData("ACCOUNT_NOT_LINKED", "account_not_linked", true)]
    [InlineData("INVALID_PAGE_TOKEN", "invalid_google_request", true)]
    [InlineData("API_PRIVATE_PREVIEW_ACCESS_DENIED", "preview_access_denied", false)]
    [InlineData("MISSING_OAUTH_SCOPE", "permission_denied", false)]
    [InlineData("[\"ACCOUNT_NOT_LINKED\"]", "account_not_linked", false)]
    public async Task Maps_google_error_reasons_without_exposing_response_details(string reason, string expected, bool directReason)
    {
        var detail = directReason
            ? new Dictionary<string, object> { ["reason"] = reason }
            : new Dictionary<string, object> { ["metadata"] = new { detailedReasons = reason } };
        var body = JsonSerializer.Serialize(new { error = new { message = "sensitive upstream detail", details = new[] { detail } } });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var client = new GoogleHealthClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => client.ReadAsync(
            "synthetic-token", "weight", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default));

        Assert.Equal(expected, exception.Message);
        Assert.Equal("weight", exception.DataType);
        Assert.Equal("data_read", exception.Stage);
        Assert.DoesNotContain("sensitive", exception.Message);
    }

    [Theory]
    [InlineData("invalid_client", "invalid_client_credentials")]
    [InlineData("redirect_uri_mismatch", "invalid_callback")]
    [InlineData("invalid_scope", "oauth_scope_configuration")]
    [InlineData("invalid_grant", "expired_signin")]
    public async Task Maps_oauth_exchange_errors_to_actionable_codes(string providerError, string expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = providerError, error_description = "do not expose" }),
                Encoding.UTF8, "application/json")
        });
        var client = new GoogleHealthClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => client.ExchangeAuthorizationCodeAsync([], default));

        Assert.Equal(expected, exception.Message);
        Assert.Equal("authorization_code", exception.Stage);
        Assert.DoesNotContain("expose", exception.Message);
    }

    [Fact]
    public async Task Invalid_refresh_grant_requires_reconnection()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
        });
        var client = new GoogleHealthClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => client.RefreshAccessTokenAsync([], default));

        Assert.Equal("reconnect_required", exception.Message);
        Assert.Equal("token_refresh", exception.Stage);
    }

    [Fact]
    public async Task Stores_encrypted_oauth_and_imports_atomically_with_tenant_isolation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connection, options => options.ExecutionStrategy(dependencies => new TestRetryingExecutionStrategy(dependencies)))
            .Options);
        await db.Database.EnsureCreatedAsync();
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid();
        db.TenantId = tenant;
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = "synthetic", DisplayName = "Synthetic", IsActive = true }); await db.SaveChangesAsync();
        var observation = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var grams = 72000;
        var tokenCalls = 0;
        var dataAuthorizations = new List<string?>();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenCalls++;
                return Json($$"""{"access_token":"synthetic-access","refresh_token":"synthetic-refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo") return Json("{\"sub\":\"synthetic-account\"}");
            if (request.RequestUri.AbsolutePath == "/revoke") return Json("{}");
            dataAuthorizations.Add(request.Headers.Authorization?.ToString());
            return Json(JsonSerializer.Serialize(new { dataPoints = new[] { new { weight = new { sampleTime = new { physicalTime = observation }, weightGrams = grams } } } }));
        });
        var protection = new EphemeralDataProtectionProvider();
        var service = new GoogleHealthService(db, protection, new GoogleHealthCoordinator(), new GoogleHealthClient(new HttpClient(handler)));
        var options = Options(); options.DataTypes = ["steps", "weight"];
        await service.SaveAsync(options, subject, default);
        var auth = await service.StartAsync(subject, default);
        var query = QueryHelpers.ParseQuery(new Uri(auth.Url).Query);
        Assert.Equal("S256", query["code_challenge_method"]); Assert.Contains("readonly", query["scope"].ToString());
        var callback = new GoogleHealthCallback { Code = "synthetic-code", State = query["state"].ToString() };
        await Assert.ThrowsAsync<GoogleHealthException>(() => service.CompleteAsync(callback, Guid.NewGuid(), default));
        await service.CompleteAsync(callback, subject, default);
        await Assert.ThrowsAsync<GoogleHealthException>(() => service.CompleteAsync(callback, subject, default));
        var status = await service.StatusAsync(default);
        Assert.Equal(["heart-rate", "weight"], status.GrantedTypes); Assert.Equal("partial_consent", status.ErrorCode);
        Assert.Equal(["steps", "sleep"], status.ErrorDataTypes);
        Assert.NotNull(status.AccessTokenExpiresAt);
        var stored = await db.GoogleHealthConnections.SingleAsync();
        Assert.DoesNotContain("synthetic-secret", stored.ProtectedSettings); Assert.DoesNotContain("synthetic-refresh", stored.ProtectedToken!);
        await service.SyncAsync(true, default); Assert.Equal(72m, (await db.GoogleHealthReadings.SingleAsync()).Value);
        Assert.Equal(1, tokenCalls);
        Assert.All(dataAuthorizations, value => Assert.Equal("Bearer synthetic-access", value));
        var protector = protection.CreateProtector("Nocturne.GoogleHealth.v1", tenant.ToString());
        stored.ProtectedToken = protector.Protect(JsonSerializer.Serialize(new
        {
            refreshToken = "synthetic-refresh",
            scopes = new[] { "openid", GoogleHealthClient.MetricsScope }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await db.SaveChangesAsync();
        grams = 73000; await service.SyncAsync(true, default);
        Assert.Equal(2, tokenCalls);
        Assert.Single(await db.GoogleHealthReadings.ToListAsync()); Assert.Equal(73m, (await db.GoogleHealthReadings.AsNoTracking().SingleAsync()).Value);
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        await service.SyncAsync(true, default); Assert.Single(await db.GoogleHealthReadings.ToListAsync());
        handler.Responder = _ => throw new InvalidOperationException("synthetic internal failure");
        await service.SyncAsync(true, default);
        Assert.Equal("internal_sync_google_read", (await service.StatusAsync(default)).ErrorCode);
        handler.Responder = request => request.RequestUri!.AbsolutePath == "/revoke"
            ? Json("{}")
            : throw new InvalidOperationException("Unexpected request after import failure");
        db.TenantId = Guid.NewGuid(); Assert.Empty(await db.GoogleHealthReadings.ToListAsync()); Assert.Empty(await db.GoogleHealthConnections.ToListAsync());
        db.TenantId = tenant;
        await service.DisconnectAsync(subject, default);
        Assert.False((await service.StatusAsync(default)).Connected); Assert.Single(await db.GoogleHealthReadings.ToListAsync());
        await service.PurgeAsync(subject, default); Assert.Empty(await db.GoogleHealthReadings.ToListAsync());

        handler.Responder = request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json(JsonSerializer.Serialize(new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", expires_in = 3600, token_type = "Bearer", scope = $"openid {GoogleHealthClient.MetricsScope} {GoogleHealthClient.ActivityScope}" })),
            "/v1/userinfo" => Json("{\"sub\":\"synthetic-account\"}"),
            var path when path.Contains("/steps/") => Json(JsonSerializer.Serialize(new { dataPoints = new[] { new { steps = new { interval = new { startTime = observation, endTime = DateTimeOffset.Parse(observation).AddMinutes(1).ToString("O") }, count = "42" } } } })),
            var path when path.Contains("/heart-rate/") => Json(JsonSerializer.Serialize(new { dataPoints = new[] { new { heartRate = new { sampleTime = new { physicalTime = observation }, beatsPerMinute = "65" } } } })),
            _ => Json(JsonSerializer.Serialize(new { dataPoints = new[] { new { weight = new { sampleTime = new { physicalTime = observation }, weightGrams = 71000 } } } }))
        };
        options.DataTypes = ["steps", "heart-rate", "weight"];
        await service.SaveAsync(options, subject, default);
        query = QueryHelpers.ParseQuery(new Uri((await service.StartAsync(subject, default)).Url).Query);
        await service.CompleteAsync(new() { State = query["state"].ToString(), Code = "synthetic-code" }, subject, default);
        await service.SyncAsync(true, default);
        Assert.Equal(3, await db.GoogleHealthReadings.CountAsync());
        Assert.Equal(42m, (await db.GoogleHealthReadings.SingleAsync(x => x.DataType == "steps")).Value);
        Assert.Equal(65m, (await db.GoogleHealthReadings.SingleAsync(x => x.DataType == "heart-rate")).Value);
        Assert.Equal(71m, (await db.GoogleHealthReadings.SingleAsync(x => x.DataType == "weight")).Value);
        var controller = new GoogleHealthController(service, db);
        foreach (var type in options.DataTypes)
            Assert.Single(Assert.IsType<List<GoogleHealthReading>>(Assert.IsType<OkObjectResult>((await controller.GetGoogleHealthReadings(type)).Result).Value));
    }

    [Fact]
    public async Task Persists_provider_errors_but_allows_empty_results_and_import_reconfiguration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid(); db.TenantId = tenant;
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = "synthetic", DisplayName = "Synthetic", IsActive = true });
        await db.SaveChangesAsync();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"synthetic-access","refresh_token":"synthetic-refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""),
            "/v1/userinfo" => Json("{\"sub\":\"synthetic-account\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"do not persist this\",\"details\":[{\"metadata\":{\"detailedReasons\":[\"ACCOUNT_NOT_LINKED\"]}}]}}", Encoding.UTF8, "application/json")
            }
        });
        var service = new GoogleHealthService(db, new EphemeralDataProtectionProvider(), new GoogleHealthCoordinator(),
            new GoogleHealthClient(new HttpClient(handler)));
        await service.SaveAsync(Options(), subject, default);
        var authorization = await service.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();
        await service.CompleteAsync(new() { State = state, Code = "synthetic-code" }, subject, default);

        await service.SyncAsync(true, default);

        var status = await service.StatusAsync(default);
        Assert.Equal("account_not_linked", status.ErrorCode);
        Assert.Equal(["weight"], status.ErrorDataTypes);
        Assert.NotNull(status.LastAttempt);
        Assert.True(status.Connected);
        Assert.DoesNotContain("persist", (await db.GoogleHealthConnections.SingleAsync()).ErrorCode);

        handler.Responder = _ => Json("{\"dataPoints\":[]}");
        await service.SyncAsync(true, default);
        status = await service.StatusAsync(default);
        Assert.Null(status.ErrorCode);
        Assert.Empty(status.ErrorDataTypes);
        Assert.NotNull(status.LastSync);

        var token = (await db.GoogleHealthConnections.SingleAsync()).ProtectedToken;
        db.GoogleHealthReadings.Add(new GoogleHealthReadingEntity
        {
            Id = Guid.CreateVersion7(), DataType = "weight", SourceKey = "existing-import",
            Mills = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(), Value = 72m, Unit = "kg"
        });
        await db.SaveChangesAsync();
        var options = Options();
        options.ClientSecret = null;
        options.DataTypes = [];
        options.ImportFrom = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await service.SaveAsync(options, subject, default);
        handler.Responder = _ => throw new InvalidOperationException("Paused imports must not call Google");
        await service.SyncAsync(true, default);
        status = await service.StatusAsync(default);
        Assert.True(status.Connected);
        Assert.Empty(status.SelectedTypes);
        Assert.Null(status.ErrorCode);
        Assert.Equal(options.ImportFrom, status.ImportFrom);
        Assert.Equal(token, (await db.GoogleHealthConnections.SingleAsync()).ProtectedToken);
        Assert.Equal(72m, (await db.GoogleHealthReadings.SingleAsync()).Value);

        options.DataTypes = ["weight"];
        await service.SaveAsync(options, subject, default);
        var readCalls = 0;
        handler.Responder = request =>
        {
            readCalls++;
            Assert.Contains("2000-01-01", QueryHelpers.ParseQuery(request.RequestUri!.Query)["filter"].ToString());
            return Json("{\"dataPoints\":[]}");
        };
        await service.SyncAsync(true, default);
        Assert.Equal(1, readCalls);
        Assert.Null((await service.StatusAsync(default)).ErrorCode);
    }

    [Fact]
    public async Task Unreadable_google_configuration_can_be_disconnected_and_reconfigured()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid(); db.TenantId = tenant;
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = "synthetic", DisplayName = "Synthetic", IsActive = true }); await db.SaveChangesAsync();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"synthetic-access","refresh_token":"synthetic-refresh","expires_in":3600,"token_type":"Bearer","scope":"openid {{GoogleHealthClient.MetricsScope}}"}"""),
            "/v1/userinfo" => Json("{\"sub\":\"synthetic-account\"}"),
            _ => Json("{}")
        });
        var coordinator = new GoogleHealthCoordinator();
        var original = new GoogleHealthService(db, new EphemeralDataProtectionProvider(), coordinator, new GoogleHealthClient(new HttpClient(handler)));
        var options = Options();
        await original.SaveAsync(options, subject, default);
        var authorization = await original.StartAsync(subject, default);
        var state = QueryHelpers.ParseQuery(new Uri(authorization.Url).Query)["state"].ToString();
        await original.CompleteAsync(new() { State = state, Code = "synthetic-code" }, subject, default);

        var recovered = new GoogleHealthService(db, new EphemeralDataProtectionProvider(), coordinator, new GoogleHealthClient(new HttpClient(handler)));
        var broken = await recovered.StatusAsync(default);
        Assert.True(broken.Connected); Assert.False(broken.Configured);
        Assert.Equal("stored_google_configuration_unreadable", broken.ErrorCode);

        await recovered.SyncAsync(true, default);
        Assert.Equal("stored_google_configuration_unreadable", (await db.GoogleHealthConnections.SingleAsync()).ErrorCode);
        Assert.True((await recovered.StatusAsync(default)).Connected);

        await recovered.DisconnectAsync(subject, default);
        Assert.False((await recovered.StatusAsync(default)).Connected);
        await recovered.SaveAsync(options, subject, default);
        Assert.True((await recovered.StatusAsync(default)).Configured);
    }

    [Fact]
    public async Task Unsupported_stored_type_keeps_status_and_disconnect_available()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid(); db.TenantId = tenant;
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = "synthetic", DisplayName = "Synthetic", IsActive = true }); await db.SaveChangesAsync();
        var provider = new EphemeralDataProtectionProvider();
        var service = new GoogleHealthService(db, provider, new GoogleHealthCoordinator(), new GoogleHealthClient(new HttpClient(new StubHandler(_ => Json("{}")))));
        await service.SaveAsync(Options(), subject, default);
        var row = await db.GoogleHealthConnections.SingleAsync();
        var legacy = Options(); legacy.DataTypes = ["weight", "body-fat"];
        var protector = provider.CreateProtector("Nocturne.GoogleHealth.v1", tenant.ToString());
        row.ProtectedSettings = protector.Protect(JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await db.SaveChangesAsync();

        var status = await service.StatusAsync(default);

        Assert.True(status.Configured); Assert.False(status.Connected);
        Assert.Equal(["weight"], status.SelectedTypes);
        Assert.Equal("unsupported_type", status.ErrorCode);
        await service.DisconnectAsync(subject, default);
    }

    [Fact]
    public void Future_catalog_entries_cannot_be_silently_selected()
    {
        var options = Options(); options.DataTypes = ["body-fat"];
        Assert.Contains(GoogleHealthClient.Capabilities, c => c.DataType == "body-fat" && !c.Supported);
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
    }

    [Fact]
    public async Task Inventory_counts_known_types_without_importing_unsupported_data()
    {
        var handler = new StubHandler(_ => Json("""{"dataPoints":[{},{}]}"""));
        var client = new GoogleHealthClient(new HttpClient(handler));

        var count = await client.CountAsync("token", "body-fat", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default);

        Assert.Equal(2, count);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("dataTypes/body-fat/dataPoints:reconcile", handler.LastRequestUri.AbsoluteUri);
    }

    [Fact]
    public void Explicit_history_start_accepts_full_history_and_rejects_future_dates()
    {
        var options = Options();
        options.ImportFrom = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        GoogleHealthService.ValidateOptions(options);

        options.ImportFrom = DateTimeOffset.UtcNow.AddDays(2);
        Assert.Throws<GoogleHealthException>(() => GoogleHealthService.ValidateOptions(options));
    }

    [Fact]
    public void Empty_import_selection_is_valid_for_pausing()
    {
        var options = Options();
        options.DataTypes = [];
        GoogleHealthService.ValidateOptions(options);
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options, new System.ComponentModel.DataAnnotations.ValidationContext(options),
            new List<System.ComponentModel.DataAnnotations.ValidationResult>(), true));
    }

    [Fact]
    public async Task Sync_returns_problem_details_when_google_is_unavailable()
    {
        var controller = new GoogleHealthController(new ThrowingGoogleHealthService(), null!);

        var result = await controller.SyncGoogleHealth(default);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("google_unavailable", problem.Detail);
    }

    private static Task ReadHistoryAsync(GoogleHealthClient client, string operation, CancellationToken ct)
    {
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = from.AddDays(1);
        return operation switch
        {
            "inventory" => client.CountAsync("synthetic-token", "weight", from, to, ct),
            "read" => client.ReadAsync("synthetic-token", "weight", from, to, ct),
            "sleep" => client.ReadSleepAsync("synthetic-token", from, to, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static GoogleHealthOptions Options() => new() { ClientId = "synthetic.apps.googleusercontent.com", ClientSecret = "synthetic-secret", CallbackUrl = "https://example.test:8450/settings/connectors/google-health/callback", DataTypes = ["weight"] };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class ThrowingGoogleHealthService : IGoogleHealthService
    {
        public Task<GoogleHealthStatus> StatusAsync(CancellationToken ct) => Task.FromResult(new GoogleHealthStatus());
        public Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task<GoogleHealthAuthorize> StartAsync(Guid subject, CancellationToken ct) => Task.FromResult(new GoogleHealthAuthorize());
        public Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task PurgeAsync(Guid subject, CancellationToken ct) => Task.CompletedTask;
        public Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct) => Task.FromResult(new GoogleHealthPreview());
        public Task QueueSyncAsync(CancellationToken ct) => throw new HttpRequestException("synthetic");
        public Task SyncAsync(bool force, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = responder;
        public Uri? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
