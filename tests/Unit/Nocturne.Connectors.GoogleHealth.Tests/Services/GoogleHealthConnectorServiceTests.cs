using System.Net;
using System.Text;
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
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
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

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsSynced[SyncDataType.BodyWeight]);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items =>
                items.Count == 1 && items.Single().Value == 72.5m),
            It.Is<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(items => items.Count == 0),
            It.Is<IReadOnlyCollection<string>>(types => types.SequenceEqual(new[] { "weight" })),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("rotated", fixture.Secrets["refreshToken"]);
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
            It.Is<IReadOnlyCollection<string>>(types => types.SequenceEqual(new[] { "weight" })),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
            var coordinator = new Mock<IGoogleHealthSyncCoordinator>();
            coordinator.Setup(value => value.Gate(tenantId)).Returns(new SemaphoreSlim(1));
            Writer = new Mock<IGoogleHealthReadingWriter>();
            Writer.Setup(value => value.WriteAsync(
                    It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
                    It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
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
        public Mock<IGoogleHealthReadingWriter> Writer { get; }
        public IReadOnlyDictionary<string, string> Secrets => secrets;

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
            SyncSleep = false
        };
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
