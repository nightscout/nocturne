using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.API.Tests.Services.Connectors;

public class NightscoutBackgroundSyncAuthTests
{
    private static NightscoutConnectorService CreateNightscoutService(
        HttpMessageHandler handler,
        NightscoutConnectorConfiguration startupDefaults)
    {
        var httpClient = new HttpClient(handler);
        return new NightscoutConnectorService(
            httpClient,
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(startupDefaults, "Nightscout"),
            new NightscoutLookups(),
            MockTenantAccessor.Create().Object,
            publisher: null);
    }

    private static GlurooConnectorService CreateGlurooService(
        HttpMessageHandler handler,
        GlurooConnectorConfiguration startupDefaults)
    {
        var httpClient = new HttpClient(handler);
        return new GlurooConnectorService(
            httpClient,
            new ConnectorServerResolver<GlurooConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<GlurooConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            new ConnectorRegistration<GlurooConnectorConfiguration>(startupDefaults, "Gluroo"),
            new NightscoutLookups(),
            MockTenantAccessor.Create().Object,
            publisher: null);
    }

    private static HttpMessageHandler RespondOkJson(string json = "[]") =>
        new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    /// <summary>A site that reads only for a request carrying a secret.</summary>
    private static HttpMessageHandler RespondOkOnlyWithASecret() =>
        new FuncHandler(request => request.Headers.Contains("api-secret")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

    /// <summary>
    ///     Nightscout can be readable without a secret, so its connector does not require one.
    ///     Gluroo Global Connect reads nothing without one, so its connector still does, and an
    ///     enabled Gluroo connector with no secret is not polled.
    /// </summary>
    [Fact]
    public void Only_Gluroo_requires_the_api_secret()
    {
        new NightscoutConnectorConfiguration { Url = "https://ns.example" }
            .MissingRequiredProperties().Should().BeEmpty();
        new GlurooConnectorConfiguration { Url = "https://app.gluroo.com" }
            .MissingRequiredProperties().Should().ContainSingle().Which.Should().Be("ApiSecret");
    }

    [Fact]
    public async Task Nightscout_BackgroundSync_UsesTenantConfigUrl_NotStartupDefaults()
    {
        var startupDefaults = new NightscoutConnectorConfiguration();
        startupDefaults.Url.Should().BeEmpty();

        var tenantConfig = new NightscoutConnectorConfiguration
        {
            Url = "https://tenant.nightscout.example.com",
            ApiSecret = "secret",
            Enabled = true,
            SyncIntervalMinutes = 5,
        };

        var service = CreateNightscoutService(RespondOkJson(), startupDefaults);

        var result = await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue("auth should succeed when tenant config URL is provided");
    }

    [Fact]
    public async Task Nightscout_BackgroundSync_WithEmptyTenantUrl_ReturnsFailure_NotException()
    {
        var startupDefaults = new NightscoutConnectorConfiguration();
        var tenantConfig = new NightscoutConnectorConfiguration { Enabled = true, SyncIntervalMinutes = 5 };

        var service = CreateNightscoutService(RespondOkJson(), startupDefaults);

        var act = async () => await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Gluroo_BackgroundSync_UsesTenantConfigUrl_NotStartupDefaults()
    {
        var startupDefaults = new GlurooConnectorConfiguration();

        var tenantConfig = new GlurooConnectorConfiguration
        {
            Url = "https://app.gluroo.com",
            ApiSecret = "gluroo-secret",
            Enabled = true,
            SyncIntervalMinutes = 5,
        };

        var service = CreateGlurooService(RespondOkJson(), startupDefaults);

        var result = await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue("auth should succeed when tenant config URL is provided");
    }

    /// <summary>
    ///     The terminal progress message belongs to the shared run wrapper. Nightscout authenticates
    ///     on the requested-range entry point, and a rejected credential returns before any data is
    ///     fetched — the run still has to hand the tenant exactly one terminal message, or the
    ///     connector's badge stays on "syncing" until the page is reloaded.
    /// </summary>
    [Theory]
    [InlineData("secret", SyncPhase.Completed)]
    [InlineData("", SyncPhase.Failed)]
    public async Task Nightscout_RequestedSync_ReportsExactlyOneTerminalMessage(
        string apiSecret, SyncPhase expectedPhase)
    {
        var service = CreateNightscoutService(RespondOkOnlyWithASecret(), new NightscoutConnectorConfiguration());
        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://tenant.nightscout.example.com",
            ApiSecret = apiSecret,
        };
        var (reporter, reported) = BuildReporter();

        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose], From = DateTime.UtcNow.AddHours(-1) },
            config, CancellationToken.None, reporter.Object);

        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(expectedPhase);
    }

    /// <summary>
    ///     Gluroo inherits <see cref="NightscoutConnectorServiceBase{TConfig}"/> whole, so the same
    ///     hand-shake applies — asserted directly rather than inferred.
    /// </summary>
    [Theory]
    [InlineData("gluroo-secret", SyncPhase.Completed)]
    [InlineData("", SyncPhase.Failed)]
    public async Task Gluroo_RequestedSync_ReportsExactlyOneTerminalMessage(
        string apiSecret, SyncPhase expectedPhase)
    {
        var service = CreateGlurooService(RespondOkOnlyWithASecret(), new GlurooConnectorConfiguration());
        var config = new GlurooConnectorConfiguration
        {
            Url = "https://app.gluroo.com",
            ApiSecret = apiSecret,
        };
        var (reporter, reported) = BuildReporter();

        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose], From = DateTime.UtcNow.AddHours(-1) },
            config, CancellationToken.None, reporter.Object);

        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(expectedPhase);
    }

    private static (Mock<ISyncProgressReporter> Reporter, List<SyncProgressEvent> Reported) BuildReporter()
    {
        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);
        return (reporter, reported);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
