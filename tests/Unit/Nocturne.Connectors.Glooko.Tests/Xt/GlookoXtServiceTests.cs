using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.Glooko.Xt;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

/// <summary>The Glooko connector with <c>Server = XT</c>: token handling, attention signals and publishing.</summary>
public class GlookoXtServiceTests
{
    private static readonly string ValidToken = GlookoXtJwtTests.Token(DateTimeOffset.UtcNow.AddDays(300).ToUnixTimeSeconds());
    private static readonly string ExpiredToken = GlookoXtJwtTests.Token(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());

    [Fact]
    public async Task Sync_WithoutAStoredToken_FailsAndAsksTheOwnerToReconnect()
    {
        var fixture = new Fixture(accessToken: null);

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not connected");
        result.Attention.Should().Be(ConnectorAttention.ReconnectRequired());
        fixture.DataClient.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Sync_WithAnExpiredToken_FailsAndAsksForANewSignIn()
    {
        var fixture = new Fixture(accessToken: ExpiredToken);

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("expired");
        result.Attention.Should().Be(ConnectorAttention.ReconnectRequired());
    }

    [Fact]
    public async Task Sync_PublishesEveryEnabledType_FromOneSession()
    {
        var fixture = new Fixture(accessToken: ValidToken);
        var at = DateTime.UtcNow.AddDays(-3);
        fixture.DataClient.Records =
        [
            new GlookoXtRecord { Id = 1, RecordedAt = at.ToString("O"), GlycemiaCgm = 120 },
            new GlookoXtRecord { Id = 2, RecordedAt = at.ToString("O"), FastInsulin = 2.5, Carbs = 30 },
            new GlookoXtRecord { Id = 3, RecordedAt = at.ToString("O"), Note = "Long walk" },
        ];
        fixture.DataClient.Export = new GlookoXtExport
        {
            Rows = [new GlookoXtExportRow { TimestampUtc = at, Event = "pumpMode - Closed loop", DurationMs = 3_600_000 }],
        };

        var now = DateTime.UtcNow;
        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = now.AddDays(-20), To = now }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.Attention.Should().BeNull();
        fixture.DataClient.Connections.Should().Be(1);
        result.ItemsSynced.Should().Contain(SyncDataType.Glucose, 1);
        result.ItemsSynced.Should().Contain(SyncDataType.Boluses, 1);
        result.ItemsSynced.Should().Contain(SyncDataType.CarbIntake, 1);
        result.ItemsSynced.Should().Contain(SyncDataType.Notes, 1);
        result.ItemsSynced.Should().Contain(SyncDataType.StateSpans, 1);
        result.ItemsSynced.Should().Contain(SyncDataType.ManualBG, 0);
    }

    [Fact]
    public async Task Sync_WithATokenExpiringWithinTheNotice_SucceedsButWarns()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(5);
        var fixture = new Fixture(accessToken: GlookoXtJwtTests.Token(expiry.ToUnixTimeSeconds()));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = DateTime.UtcNow.AddDays(-1), To = DateTime.UtcNow }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Attention!.Kind.Should().Be(ConnectorAttentionKind.ReconnectSoon);
        result.Attention.Deadline.Should().BeCloseTo(expiry.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sync_WhenTheServerRefusesTheToken_FailsWithAReconnectMessage()
    {
        var fixture = new Fixture(accessToken: ValidToken);
        fixture.DataClient.ConnectFailure = new GlookoXtAuthenticationException("refused");

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = DateTime.UtcNow.AddDays(-1), To = DateTime.UtcNow }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("sign in again");
        result.Attention.Should().Be(ConnectorAttention.ReconnectRequired());
    }

    [Fact]
    public void XtConfiguration_RequiresTheToken_NotThePassword()
    {
        new GlookoConnectorConfiguration { Server = "XT", Email = "p@example.com" }.MissingRequiredProperties()
            .Should().Equal("AccessToken");
        new GlookoConnectorConfiguration { Server = "EU", Email = "p@example.com" }.MissingRequiredProperties()
            .Should().Equal("Password");
    }

    private sealed class Fixture
    {
        public GlookoConnectorConfiguration Config { get; }
        public GlookoConnectorService Service { get; }
        public FakeGlookoXtDataClient DataClient { get; } = new();
        public Mock<IConnectorPublisher> Publisher { get; } = new();

        public Fixture(string? accessToken)
        {
            Config = new GlookoConnectorConfiguration { Server = GlookoConstants.RegionXT, Email = "patient@example.com", AccessToken = accessToken };

            Publisher.Setup(p => p.IsAvailable).Returns(true);
            Publisher.Setup(p => p.Glucose.PublishSensorGlucoseAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishBolusesAsync(It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishCarbIntakesAsync(It.IsAny<IEnumerable<CarbIntake>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishBGChecksAsync(It.IsAny<IEnumerable<BGCheck>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishBolusCalculationsAsync(It.IsAny<IEnumerable<BolusCalculation>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishTempBasalsAsync(It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Treatments.PublishBasalInjectionsAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Device.PublishDeviceEventsAsync(It.IsAny<IEnumerable<DeviceEvent>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Metadata.PublishNotesAsync(It.IsAny<IEnumerable<Note>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Metadata.PublishStateSpansAsync(It.IsAny<IEnumerable<Nocturne.Core.Models.StateSpan>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Metadata.PublishProfilesAsync(It.IsAny<IEnumerable<Nocturne.Core.Models.Profile>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Metadata.PublishSystemEventsAsync(It.IsAny<IEnumerable<Nocturne.Core.Models.SystemEvent>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
            Publisher.Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);

            var resolver = new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null);
            var tenant = new Mock<ITenantAccessor>();
            tenant.Setup(t => t.IsResolved).Returns(true);
            tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var tokenProvider = new GlookoAuthTokenProvider(
                new HttpClient(), new ConnectorTokenCache(), resolver, tenant.Object,
                NullLogger<GlookoAuthTokenProvider>.Instance, Mock.Of<IRetryDelayStrategy>());

            Service = new GlookoConnectorService(
                new HttpClient(), resolver, NullLogger<GlookoConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(), Mock.Of<IRateLimitingStrategy>(), tokenProvider,
                Publisher.Object, xtDataClient: DataClient);
        }
    }
}
