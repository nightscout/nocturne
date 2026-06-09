using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;

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
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(startupDefaults, "Nightscout"),
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
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<GlurooConnectorConfiguration>(startupDefaults, "Gluroo"),
            publisher: null);
    }

    private static HttpMessageHandler RespondOkJson(string json = "[]") =>
        new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

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

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
