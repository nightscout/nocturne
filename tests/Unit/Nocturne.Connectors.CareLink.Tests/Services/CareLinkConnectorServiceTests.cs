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
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
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

    /// <summary>
    /// A payload with no sensor readings still records a zero for glucose: the tenant's sync card
    /// renders a badge per key, so a missing key reads as "never checked" rather than "checked,
    /// found nothing".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGlucoseIsActiveButEmpty_RecordsAnExplicitZero()
    {
        // lastSG present (so the monitor payload is used) but no sgs array, and no medical-device
        // update time, so the data does not read as stale and the glucose step does run.
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastSG": {}
                }
                """
        };
        var fixture = new ServiceFixture(handler, new CareLinkConnectorConfiguration
        {
            Username = "user@example.com",
            Server = "EU",
            SyncDeviceStatus = false,
            SyncBoluses = false,
            SyncCarbIntake = false,
            SyncTempBasals = false,
            SyncDeviceEvents = false,
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
        });
    }

    /// <summary>
    /// Every reading in the payload is counted, not merely the batch existing at all: a count that
    /// silently drops records is how a tenant comes to believe a gap in their glucose history was
    /// never uploaded.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_CountsEverySensorGlucoseRecordPublished()
    {
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastSG": { "sg": 120, "datetime": "2026-01-01T10:00:00", "kind": "SG" },
                  "sgs": [
                    { "sg": 100, "datetime": "2026-01-01T09:50:00", "kind": "SG" },
                    { "sg": 110, "datetime": "2026-01-01T09:55:00", "kind": "SG" },
                    { "sg": 120, "datetime": "2026-01-01T10:00:00", "kind": "SG" }
                  ]
                }
                """
        };
        var fixture = new ServiceFixture(handler, GlucoseOnlyConfiguration(), withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 3,
        });
        fixture.PublishedGlucose.Should().HaveCount(3);
    }

    /// <summary>
    /// Device status is mapped from the same payload every cycle, so an active toggle always
    /// reports exactly the one record it published.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_CountsTheDeviceStatusItPublishes()
    {
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastSG": {}
                }
                """
        };
        var config = GlucoseOnlyConfiguration();
        config.SyncDeviceStatus = true;
        var fixture = new ServiceFixture(handler, config, withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.DeviceStatus] },
            fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
            [SyncDataType.DeviceStatus] = 1,
        });
        fixture.PublishedDeviceStatuses.Should().ContainSingle();
    }

    /// <summary>
    /// A payload too old to publish has still been checked, so glucose reports a zero rather than
    /// leaving the tenant a missing badge that reads as "never checked".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheDataIsStale_RecordsAnExplicitZero()
    {
        // A medical-device update an hour behind the server clock is past the staleness threshold,
        // so the readings in the payload are deliberately not published.
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastMedicalDeviceDataUpdateServerTime": 1767258000000,
                  "lastSG": { "sg": 120, "datetime": "2026-01-01T09:00:00", "kind": "SG" },
                  "sgs": [
                    { "sg": 120, "datetime": "2026-01-01T09:00:00", "kind": "SG" }
                  ]
                }
                """
        };
        var fixture = new ServiceFixture(handler, GlucoseOnlyConfiguration(), withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
        });
        fixture.PublishedGlucose.Should().BeEmpty("stale readings are deliberately not published");
    }

    /// <summary>
    /// The zero the stale path records goes through <c>RecordPublishOutcome</c>, which deliberately
    /// has no active-type check of its own — so the step's own toggle gate is the only thing keeping
    /// a switched-off type from being badged, and it has to stay.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGlucoseIsDisabledAndTheDataIsStale_RecordsNothing()
    {
        var handler = new CareLinkFakeHandler
        {
            MonitorDataJson = """
                {
                  "currentServerTime": 1767261600000,
                  "lastMedicalDeviceDataUpdateServerTime": 1767258000000,
                  "lastSG": { "sg": 120, "datetime": "2026-01-01T09:00:00", "kind": "SG" }
                }
                """
        };
        var config = GlucoseOnlyConfiguration();
        config.SyncGlucose = false;
        var fixture = new ServiceFixture(handler, config, withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.ItemsSynced.Should().BeEmpty(
            "a type the tenant switched off is never reported, stale payload or not");
    }

    /// <summary>Leaves only the glucose step able to publish.</summary>
    private static CareLinkConnectorConfiguration GlucoseOnlyConfiguration() => new()
    {
        Username = "user@example.com",
        Server = "EU",
        SyncDeviceStatus = false,
        SyncBoluses = false,
        SyncCarbIntake = false,
        SyncTempBasals = false,
        SyncDeviceEvents = false,
    };

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
        internal List<SensorGlucose> PublishedGlucose { get; } = [];
        internal List<Nocturne.Core.Models.DeviceStatus> PublishedDeviceStatuses { get; } = [];

        internal ServiceFixture(
            CareLinkFakeHandler handler,
            CareLinkConnectorConfiguration? config = null,
            bool withPublisher = false)
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

            IConnectorPublisher? publisher = null;
            if (withPublisher)
            {
                var glucose = new Mock<IGlucosePublisher>();
                glucose
                    .Setup(p => p.PublishSensorGlucoseAsync(
                        It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                        It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                    .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                        (batch, _, _, _) => PublishedGlucose.AddRange(batch))
                    .ReturnsAsync(true);

                var device = new Mock<IDevicePublisher>();
                device
                    .Setup(p => p.PublishDeviceStatusAsync(
                        It.IsAny<IEnumerable<Nocturne.Core.Models.DeviceStatus>>(), It.IsAny<string>(),
                        It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                    .Callback<IEnumerable<Nocturne.Core.Models.DeviceStatus>, string, WriteOrigin, CancellationToken>(
                        (batch, _, _, _) => PublishedDeviceStatuses.AddRange(batch))
                    .ReturnsAsync(true);

                var mock = new Mock<IConnectorPublisher>();
                mock.Setup(p => p.IsAvailable).Returns(true);
                mock.Setup(p => p.Glucose).Returns(glucose.Object);
                mock.Setup(p => p.Device).Returns(device.Object);
                mock.Setup(p => p.Treatments).Returns(Mock.Of<ITreatmentPublisher>());
                mock.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());
                publisher = mock.Object;
            }

            Service = new CareLinkConnectorService(
                new HttpClient(handler),
                serverResolver,
                tokenProvider,
                configService.Object,
                NullLogger<CareLinkConnectorService>.Instance,
                publisher);
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
