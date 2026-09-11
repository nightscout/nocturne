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
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthTests
{
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
        Assert.True(coordinator.StartQueued(tenantId));
        coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);

        var progress = Assert.IsType<GoogleHealthCoordinator.SyncProgress>(coordinator.Progress(tenantId));
        Assert.Equal((GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3),
            (progress.Phase, progress.DataType, progress.CompletedDataTypes,
                progress.TotalDataTypes, progress.PagesRead));
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
        var service = Service(store, handler, tenantId);
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
        await db.SaveChangesAsync();
        var writer = new GoogleHealthReadingWriter(
            Mock.Of<IHeartRateService>(), Mock.Of<IStepCountService>(),
            Mock.Of<IBodyWeightService>(), Mock.Of<ISleepService>(), db);

        await writer.WriteAsync([], [], ["weight"],
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 2, default);

        Assert.Single(await db.HeartRates.AsNoTracking().ToListAsync());
        Assert.Single(await db.BodyWeights.AsNoTracking().ToListAsync());
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
            Mock.Of<ISleepService>(), db);
        var readings = Enumerable.Range(0, 5).Select(index => new GoogleHealthReading
        {
            DataType = "heart-rate", Mills = index, Value = 60 + index
        }).ToArray();

        await writer.WriteAsync(readings, [], ["heart-rate"],
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 2, default);

        Assert.Equal([2, 2, 1], batches.Select(batch => batch.Length));
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

    private static GoogleHealthService Service(
        TestConnectorStore store,
        HttpMessageHandler handler,
        Guid tenantId)
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(value => value.IsResolved).Returns(true);
        tenant.SetupGet(value => value.TenantId).Returns(tenantId);
        return new GoogleHealthService(
            new GoogleHealthCoordinator(),
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
                    response = new ConnectorConfigurationResponse
                    {
                        ConnectorName = "GoogleHealth",
                        Configuration = configuration,
                        IsActive = true
                    };
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
