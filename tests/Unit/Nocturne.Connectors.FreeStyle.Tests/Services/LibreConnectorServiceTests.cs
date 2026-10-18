using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Services;

public class LibreConnectorServiceTests
{
    [Fact]
    public async Task SyncDataAsync_ReauthenticationFails_ReportsAFailedSyncRatherThanAnEmptyOne()
    {
        var fixture = new ServiceFixture(tokenToReturn: null);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        fixture.Publisher.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<WriteOrigin>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncDataAsync_ReauthenticationSucceedsWithNoNewReadings_ReportsSuccess()
    {
        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            connectionsJson: "{\"data\":[{\"patientId\":\"p1\",\"firstName\":\"A\",\"lastName\":\"B\"}]}",
            graphJson: "{\"data\":{\"connection\":{\"glucoseMeasurement\":"
                       + "{\"factoryTimestamp\":\"2024-01-01T00:00:00.000Z\",\"valueInMgPerDl\":0,\"trendArrow\":0}},"
                       + "\"graphData\":[]}}");

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(0);
    }

    private const string Connections = "{\"data\":[{\"patientId\":\"p1\",\"firstName\":\"A\",\"lastName\":\"B\"}]}";

    private static string GraphWithSensor(string sensorJson) =>
        "{\"data\":{\"connection\":{\"glucoseMeasurement\":"
        + "{\"factoryTimestamp\":\"2024-01-01T00:00:00.000Z\",\"valueInMgPerDl\":0,\"trendArrow\":0},"
        + "\"sensor\":" + sensorJson + "},\"graphData\":[]}}";

    /// <summary>
    /// LibreLinkUp names the sensor it is reading and when it was activated, so its start is
    /// recorded rather than left to inference, even on a sync with no new readings.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_PublishesTheCurrentSensorsStart()
    {
        var fixture = new ServiceFixture(
            connectionsJson: Connections,
            graphJson: GraphWithSensor("{\"deviceId\":\"d1\",\"sn\":\"0M0001ABCD\",\"a\":1760000000,\"w\":60,\"pt\":4}"));
        List<DeviceEvent> published = [];
        fixture.Publisher
            .Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow.AddDays(-1));
        fixture.Publisher
            .Setup(p => p.Device.PublishDeviceEventsAsync(
                It.IsAny<IEnumerable<DeviceEvent>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<DeviceEvent> records, string _, WriteOrigin _, CancellationToken _) => published.AddRange(records))
            .ReturnsAsync(true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.DeviceEvents] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        published.Should().ContainSingle().Which.Should().Match<DeviceEvent>(e =>
            e.EventType == DeviceEventType.SensorStart
            && e.Timestamp == DateTimeOffset.FromUnixTimeSeconds(1760000000).UtcDateTime
            && e.SyncIdentifier == "libre_sensor_0M0001ABCD"
            && e.Device!.Contains("0M0001ABCD"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"sn\":\"\",\"a\":1760000000}")]
    [InlineData("{\"sn\":\"0M0001ABCD\",\"a\":0}")]
    public async Task SyncDataAsync_WithNoActivatedSensor_PublishesNoStart(string sensorJson)
    {
        var fixture = new ServiceFixture(connectionsJson: Connections, graphJson: GraphWithSensor(sensorJson));

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.DeviceEvents] }, fixture.Config, CancellationToken.None);

        fixture.Publisher.Verify(p => p.Device.PublishDeviceEventsAsync(
            It.IsAny<IEnumerable<DeviceEvent>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeLibreAuthTokenProvider(string? tokenToReturn) : LibreLinkAuthTokenProvider(
        new HttpClient(),
        new ConnectorTokenCache(),
        new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(null, null, null),
        new FakeTenantAccessor(),
        NullLogger<LibreLinkAuthTokenProvider>.Instance,
        Mock.Of<IRetryDelayStrategy>())
    {
        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                tokenToReturn is null ? (null, DateTime.MinValue, null) : (tokenToReturn, DateTime.UtcNow.AddHours(1), null));

        private sealed class FakeTenantAccessor : ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public TenantContext? Context => null;
            public void SetTenant(TenantContext context) { }
        }
    }

    private sealed class RoutingHandler(string? connectionsJson, string? graphJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.EndsWith("/graph", StringComparison.OrdinalIgnoreCase) && graphJson != null)
                return Task.FromResult(Json(graphJson));

            if (path.Contains(LibreLinkUpConstants.ApiPaths.Connections, StringComparison.OrdinalIgnoreCase)
                && connectionsJson != null)
                return Task.FromResult(Json(connectionsJson));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ServiceFixture
    {
        public LibreConnectorService Service { get; }
        public LibreLinkUpConnectorConfiguration Config { get; }
        public Mock<IConnectorPublisher> Publisher { get; }

        public ServiceFixture(
            string? tokenToReturn = "valid-token",
            string? connectionsJson = null,
            string? graphJson = null)
        {
            Config = new LibreLinkUpConnectorConfiguration
            {
                Username = "test@example.com",
                Password = "test-password",
            };

            var httpClient = new HttpClient(new RoutingHandler(connectionsJson, graphJson))
            {
                BaseAddress = new Uri("https://api-eu.libreview.io")
            };

            Publisher = new Mock<IConnectorPublisher>();
            Publisher.Setup(p => p.IsAvailable).Returns(true);

            var tokenProvider = new FakeLibreAuthTokenProvider(tokenToReturn);

            Service = new LibreConnectorService(
                httpClient,
                new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(null, null, null),
                NullLogger<LibreConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                tokenProvider,
                Publisher.Object);
        }
    }
}
