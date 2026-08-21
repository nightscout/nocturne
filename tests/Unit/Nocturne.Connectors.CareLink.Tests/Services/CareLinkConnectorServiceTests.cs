using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Services;

public class CareLinkConnectorServiceTests
{
    /// <summary>
    /// Authentication succeeds but every data endpoint fails. A working CareLink account always
    /// returns a payload — even with no current readings — so no payload at all means the fetch
    /// failed, and the sync must say so. Reporting success marked the connector healthy while
    /// nothing reached the tenant, which is how a totally broken connector went unnoticed.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenEveryDataEndpointFails_ReportsFailureNotSuccess()
    {
        var handler = new CareLinkFakeHandler();
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse(
            "a sync that obtained no data from any endpoint has not succeeded");
        result.Errors.Should().ContainMatch("*No data returned from any CareLink endpoint*");
    }

    /// <summary>
    /// A rejected treatment publish must fail the sync. The bolus publish used to gate only the
    /// <c>ItemsSynced</c> counter, so a connector that reached CareLink but could not write to the
    /// tenant still reported a green sync with the treatments missing.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenBolusPublishIsRejected_ReportsFailure()
    {
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastSG": {},
                  "markers": [
                    {
                      "type": "INSULIN",
                      "dateTime": "2026-01-01T10:00:00",
                      "id": 1,
                      "bolusType": "NORMAL",
                      "programmedFastAmount": 2.5,
                      "deliveredFastAmount": 2.5
                    }
                  ]
                }
                """
        };
        // Boluses alone, so the assertion cannot be satisfied by another step's failure. No
        // publisher is wired, so every publish is rejected.
        var fixture = new ServiceFixture(handler, new CareLinkConnectorConfiguration
        {
            Username = "user@example.com",
            Server = "EU",
            SyncGlucose = false,
            SyncDeviceStatus = false,
            SyncCarbIntake = false,
            SyncTempBasals = false,
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Boluses] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse("a bolus batch that never reached the tenant is not a successful sync");
        result.Errors.Should().Contain("Boluses publish failed");
    }

    /// <summary>
    /// A rejected alarm publish must fail the sync and leave the dedup key unadvanced, so the next
    /// cycle retries the same alarm instead of treating it as delivered.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAlarmPublishIsRejected_ReportsFailureAndRetriesNextSync()
    {
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastSG": {},
                  "lastAlarm": {
                    "type": "PUMP_SUSPEND",
                    "code": 816,
                    "flash": true,
                    "datetime": "2026-01-01T10:00:00"
                  }
                }
                """
        };
        var fixture = new ServiceFixture(handler, AlarmOnlyConfiguration());

        var first = await fixture.Service.SyncDataAsync(
            new SyncRequest(), fixture.Config, CancellationToken.None);
        var second = await fixture.Service.SyncDataAsync(
            new SyncRequest(), fixture.Config, CancellationToken.None);

        first.Success.Should().BeFalse("an alarm that never reached the tenant is not a successful sync");
        first.Errors.Should().Contain("DeviceEvents publish failed");
        second.Errors.Should().Contain("DeviceEvents publish failed",
            "the dedup key must not advance past an alarm that was never published");
        second.ItemsSynced.GetValueOrDefault(SyncDataType.DeviceEvents).Should().Be(1,
            "the second cycle must attempt the same alarm again");
    }

    /// <summary>
    /// Both system-event paths — the last alarm and the notification history — are gated and counted
    /// under the DeviceEvents toggle, which a CareLink tenant that never saw the toggle has on.
    /// </summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task SyncDataAsync_GatesSystemEventsOnTheDeviceEventsToggle(
        bool syncDeviceEvents, int expectedCount)
    {
        var config = AlarmOnlyConfiguration();
        config.SyncDeviceEvents = syncDeviceEvents;
        var fixture = new ServiceFixture(SystemEventHandler(NotificationBeforeAlarm), config);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), fixture.Config, CancellationToken.None);

        result.ItemsSynced.GetValueOrDefault(SyncDataType.DeviceEvents).Should().Be(expectedCount);
        result.Errors.Contains("DeviceEvents publish failed").Should().Be(expectedCount > 0,
            "a switched-off type is never handed to the publisher, so it cannot fail");
    }

    private const string AlarmDateTime = "2026-01-01T10:00:00";
    private const string NotificationBeforeAlarm = "2026-01-01T09:55:00";

    /// <summary>A payload feeding both system-event paths: the last alarm and one notification.</summary>
    private static CareLinkFakeHandler SystemEventHandler(string notificationDateTime) => new()
    {
        // Server time equals the alarm time, so no pump offset is applied and the strings are UTC.
        MonitorDataJson = $$"""
            {
              "currentServerTime": 1767261600000,
              "lastSG": {},
              "lastAlarm": {
                "type": "PUMP_SUSPEND",
                "code": 816,
                "flash": true,
                "datetime": "{{AlarmDateTime}}"
              },
              "notificationHistory": {
                "activeNotifications": [
                  {
                    "referenceGUID": "11111111-1111-1111-1111-111111111111",
                    "triggeredDateTime": "{{notificationDateTime}}",
                    "type": "ALERT",
                    "faultId": 105,
                    "messageId": "BC_SID_LOW_RESERVOIR"
                  }
                ]
              }
            }
            """
    };

    /// <summary>
    /// A run that never got past authentication reports the shared failure shape: the summary in
    /// <c>Message</c> for the tenant's sync card and the source-qualified detail in <c>Errors</c>,
    /// which is what gets persisted as the connector's last error. CareLink authenticates inside
    /// its own sync body, so it has to opt into that shape rather than inherit it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAuthenticationFails_ReportsTheSharedFailureShape()
    {
        // A token response carrying no access token leaves the connector unauthenticated.
        var fixture = new ServiceFixture(new CareLinkFakeHandler { TokenResponseJson = "{}" });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Authentication failed");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be($"Authentication failed for {DataSources.CareLinkConnector}");
    }

    /// <summary>Leaves only the alarm step able to publish, so its failure cannot be confused for another step's.</summary>
    private static CareLinkConnectorConfiguration AlarmOnlyConfiguration() => new()
    {
        Username = "user@example.com",
        Server = "EU",
        SyncGlucose = false,
        SyncDeviceStatus = false,
        SyncBoluses = false,
        SyncCarbIntake = false,
        SyncTempBasals = false,
    };

    /// <summary>Wires the connector service and a real token provider onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal CareLinkConnectorService Service { get; }
        internal CareLinkConnectorConfiguration Config { get; }

        internal ServiceFixture(CareLinkFakeHandler handler, CareLinkConnectorConfiguration? config = null)
        {
            Config = config ?? new CareLinkConnectorConfiguration
            {
                Username = "user@example.com",
                Server = "EU",
            };

            var tenantAccessor = new Mock<ITenantAccessor>();
            tenantAccessor.Setup(t => t.IsResolved).Returns(true);
            tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var configService = new Mock<IConnectorConfigurationService>();
            configService
                .Setup(s => s.GetSecretsAsync("CareLink", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string> { ["refresh_token"] = "stored-refresh-token" });

            var serverResolver = new ConnectorServerResolver<CareLinkConnectorConfiguration>(
                null, null, CareLinkConstants.Servers.Eu);

            var tokenProvider = new HandlerBackedTokenProvider(
                new HttpClient(handler),
                new ConnectorTokenCache(),
                serverResolver,
                tenantAccessor.Object,
                NullLogger<CareLinkAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                handler);

            Service = new CareLinkConnectorService(
                new HttpClient(handler),
                serverResolver,
                tokenProvider,
                configService.Object,
                NullLogger<CareLinkConnectorService>.Instance);
        }
    }

    private sealed class HandlerBackedTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<CareLinkConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<CareLinkAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy,
        HttpMessageHandler handler)
        : CareLinkAuthTokenProvider(httpClient, tokenCache, serverResolver, tenantAccessor, logger, retryDelayStrategy)
    {
        protected override CareLinkAuthFlowService CreateAuthFlow() => new(NullLogger.Instance, handler);
    }
}
