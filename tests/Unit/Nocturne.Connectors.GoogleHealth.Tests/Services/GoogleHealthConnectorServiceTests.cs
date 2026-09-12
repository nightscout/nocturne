using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthConnectorServiceTests
{
    [Fact]
    public async Task Sync_requires_a_durable_oauth_session()
    {
        var fixture = new Fixture(_ => Json("{}"));
        var config = fixture.Configuration();
        config.RefreshToken = null;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("reconnect_required", result.Message);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_fetches_selected_data_and_publishes_to_native_health_services()
    {
        var sampleTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"rotated","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"TIME"},"weightGrams":72500}}]}"""
                    .Replace("TIME", sampleTime.ToString("O"))),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsSynced[SyncDataType.BodyWeight]);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items =>
                items.Count == 1 && items.Single().Value == 72.5m),
            It.Is<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(items => items.Count == 0),
            2,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("rotated", fixture.Secrets["refreshToken"]);
    }

    [Fact]
    public async Task Successful_sync_persists_its_own_resume_watermark()
    {
        var sampleTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"rotated","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"TIME"},"weightGrams":72500}}]}"""
                    .Replace("TIME", sampleTime.ToString("O"))),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("lastSyncedTo", fixture.LastSavedConfiguration);

        var repeated = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(repeated.Success);
    }

    [Theory]
    [InlineData("2026-09-10T10:00:00Z")]
    [InlineData("2026-09-10T12:00:00+02:00")]
    [InlineData("2026-09-10T10:00:00")]
    public async Task Scheduled_sync_resumes_from_a_persisted_watermark_in_utc(string watermark)
    {
        var requestedFrom = DateTimeOffset.MinValue;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, value => requestedFrom = value),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null, lastSyncedTo = watermark });

        var result = await fixture.Service.SyncDataAsync(fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 9, 55, 0, TimeSpan.Zero), requestedFrom);
    }

    [Theory]
    [InlineData("2026-09-10T10:00:00Z")]
    [InlineData("2026-09-10T12:00:00+02:00")]
    [InlineData("2026-09-10T10:00:00")]
    public async Task Older_manual_backfill_does_not_move_the_watermark_backwards(string watermark)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null, lastSyncedTo = watermark });
        var result = await fixture.Service.SyncDataAsync(new SyncRequest
        {
            From = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
        }, fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        using var stored = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.Equal(watermark, stored.RootElement.GetProperty("lastSyncedTo").GetString());
        Assert.Null(fixture.LastSavedConfiguration);
    }

    [Fact]
    public async Task Requested_data_types_narrow_the_configured_selection()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncSteps = true;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.BodyWeight] },
            config,
            CancellationToken.None);

        Assert.True(result.Success);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scheduled_sync_uses_the_bounded_lookback_not_the_initial_import_date()
    {
        var requestedFrom = DateTimeOffset.MinValue;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, value => requestedFrom = value),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.ImportFrom = "2000-01-01T00:00:00.0000000+00:00";
        config.HistoryDays = 7;

        var result = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.InRange(requestedFrom, DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddDays(-6));
    }

    [Fact]
    public async Task Sync_writes_each_page_before_reconciling_the_completed_type()
    {
        var calls = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(++calls == 1
                ? """{"dataPoints":[{"name":"first","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}],"nextPageToken":"next"}"""
                : """{"dataPoints":[{"name":"second","weight":{"sampleTime":{"physicalTime":"2026-09-01T11:00:00Z"},"weightGrams":71000}}]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ItemsSynced[SyncDataType.BodyWeight]);
        Assert.Equal(["WriteAsync", "WriteAsync", "ReconcileAsync"],
            fixture.Writer.Invocations.Select(invocation => invocation.Method.Name));
    }

    [Fact]
    public async Task Sync_does_not_reconcile_when_a_later_page_fails()
    {
        var calls = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => ++calls == 1
                ? Json("""{"dataPoints":[{"name":"first","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}],"nextPageToken":"next"}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.False(result.Success);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(readings => readings.Count == 1),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Writer.Verify(value => value.ReconcileAsync(
            It.IsAny<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(),
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Manual_backfill_consumes_the_import_start_date_after_success()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.ImportFrom = "2000-01-01T00:00:00.0000000+00:00";

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(fixture.ImportFromWasConsumed);
    }

    [Fact]
    public async Task Sleep_import_preserves_overnight_stages_and_completes_repeated_runs()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.SleepScope}}"}"""),
            var path when path.Contains("/sleep/") => Json("""
                {"dataPoints":[{"name":"night-1","sleep":{
                  "interval":{"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T06:00:00Z"},
                  "stages":[
                    {"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T02:00:00Z","type":"DEEP"},
                    {"startTime":"2026-09-05T02:00:00Z","endTime":"2026-09-05T06:00:00Z","type":"REM"}
                  ]}}]}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncBodyWeight = false;
        config.SyncSleep = true;
        config.GrantedScopes = GoogleHealthClient.SleepScope;
        var from = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var request = new SyncRequest { From = from, To = from.AddDays(1) };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await fixture.Service.SyncDataAsync(request, config, default);
            Assert.True(result.Success);
            Assert.Equal(1, result.ItemsSynced[SyncDataType.Sleep]);
        }

        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items => items.Count == 0),
            It.Is<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(items =>
                items.Count == 1 && items.Single().StartTime == from.AddHours(-2) &&
                items.Single().EndTime == from.AddHours(6) && items.Single().Stages!.Count == 2),
            2, It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Writer.Verify(value => value.ReconcileAsync(
            It.IsAny<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(),
            It.Is<IReadOnlyCollection<string>>(identifiers => identifiers.Count == 1),
            It.Is<IReadOnlyCollection<string>>(types => types.Contains("sleep")),
            new DateTimeOffset(from), new DateTimeOffset(from.AddDays(1)), It.IsAny<CancellationToken>()), Times.Exactly(2));
        using var saved = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.Equal(from.AddDays(1), saved.RootElement.GetProperty("lastSyncedTo").GetDateTime());
    }

    private sealed class Fixture
    {
        private readonly Guid tenantId = Guid.NewGuid();
        private Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["refreshToken"] = "refresh",
            ["grantedScopes"] = GoogleHealthClient.MetricsScope
        };

        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var tenant = new Mock<ITenantAccessor>();
            tenant.SetupGet(value => value.IsResolved).Returns(true);
            tenant.SetupGet(value => value.TenantId).Returns(tenantId);
            var configurations = new Mock<IConnectorConfigurationService>();
            configurations.Setup(value => value.GetSecretsAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase));
            configurations.Setup(value => value.SaveSecretsAsync(
                    "GoogleHealth", It.IsAny<Dictionary<string, string>>(), null,
                    It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, string?, CancellationToken>((_, values, _, _) =>
                    secrets = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase))
                .Returns(Task.CompletedTask);
            configurations.Setup(value => value.GetConfigurationAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ConnectorConfigurationResponse
                {
                    ConnectorName = "GoogleHealth",
                    Configuration = JsonDocument.Parse(StoredConfiguration)
                });
            configurations.Setup(value => value.SaveConfigurationAsync(
                    "GoogleHealth", It.IsAny<JsonDocument>(), null, It.IsAny<CancellationToken>()))
                .Callback<string, JsonDocument, string?, CancellationToken>((_, document, _, _) =>
                {
                    ImportFromWasConsumed = document.RootElement.GetProperty("importFrom").ValueKind == JsonValueKind.Null;
                    LastSavedConfiguration = document.RootElement.GetRawText();
                    StoredConfiguration = LastSavedConfiguration;
                })
                .ReturnsAsync(() => new ConnectorConfigurationResponse());
            var coordinator = Coordinator;
            coordinator.Setup(value => value.Gate(tenantId)).Returns(new SemaphoreSlim(1));
            Writer = new Mock<IGoogleHealthReadingWriter>();
            Writer.Setup(value => value.WriteAsync(
                    It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
                    It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Writer.Setup(value => value.ReconcileAsync(
                    It.IsAny<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var handler = new StubHandler(responder);
            var oauth = new GoogleHealthAuthTokenProvider(
                new HttpClient(handler, false),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                tenant.Object,
                NullLogger<GoogleHealthAuthTokenProvider>.Instance);
            Service = new GoogleHealthConnectorService(
                new HttpClient(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                new GoogleHealthClient(new HttpClient(handler, false)),
                oauth,
                Writer.Object,
                coordinator.Object,
                configurations.Object,
                tenant.Object,
                NullLogger<GoogleHealthConnectorService>.Instance);
        }

        public GoogleHealthConnectorService Service { get; }
        public Mock<IGoogleHealthSyncCoordinator> Coordinator { get; } = new();
        public Mock<IGoogleHealthReadingWriter> Writer { get; }
        public IReadOnlyDictionary<string, string> Secrets => secrets;
        public bool ImportFromWasConsumed { get; private set; }
        public string? LastSavedConfiguration { get; private set; }
        public string StoredConfiguration { get; set; } = "{\"importFrom\":\"2000-01-01T00:00:00.0000000+00:00\"}";

        public GoogleHealthConnectorConfiguration Configuration() => new()
        {
            ClientId = "client.apps.googleusercontent.com",
            ClientSecret = "secret",
            CallbackUrl = "https://example.test/settings/connectors/google-health/callback",
            RefreshToken = secrets["refreshToken"],
            GrantedScopes = secrets["grantedScopes"],
            SyncSteps = false,
            SyncHeartRate = false,
            SyncBodyWeight = true,
            SyncSleep = false,
            BatchSize = 2
        };
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage CaptureRange(
        HttpRequestMessage request,
        Action<DateTimeOffset> capture)
    {
        var filter = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["filter"]!;
        var timestamp = filter.Split('"')[1];
        capture(DateTimeOffset.Parse(timestamp));
        return Json("{\"dataPoints\":[]}");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
