using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

public class NocturneRemoteBackgroundSyncAuthTests
{
    private static NocturneRemoteConnectorService CreateService(
        HttpMessageHandler handler,
        NocturneRemoteConnectorConfiguration startupDefaults)
    {
        var httpClient = new HttpClient(handler);
        return new NocturneRemoteConnectorService(
            httpClient,
            new ConnectorServerResolver<NocturneRemoteConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NocturneRemoteConnectorService>>(),
            new ConnectorRegistration<NocturneRemoteConnectorConfiguration>(startupDefaults, "NocturneRemote"),
            Mock.Of<IRetryDelayStrategy>(),
            publisher: null);
    }

    /// <summary>
    ///     Returns the right empty-collection JSON based on the request path so that both
    ///     paginated V4 endpoints (expect PaginatedResponse&lt;T&gt;) and array endpoints
    ///     (DeviceStatus v1, Food v4) can deserialize successfully.
    /// </summary>
    private static HttpMessageHandler RespondOkJson() =>
        new FuncHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            // v1 DeviceStatus and v4 Foods return a flat JSON array; everything else is paginated
            var isArrayEndpoint = path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/v4/foods", StringComparison.OrdinalIgnoreCase);

            var json = isArrayEndpoint
                ? "[]"
                : "{\"data\":[],\"pagination\":{\"total\":0,\"limit\":500,\"offset\":0}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

    [Fact]
    public async Task BackgroundSync_UsesTenantConfigUrl_NotStartupDefaults()
    {
        var startupDefaults = new NocturneRemoteConnectorConfiguration();

        var tenantConfig = new NocturneRemoteConnectorConfiguration
        {
            Url = "https://remote.nocturne.example.com",
            Token = "bearer-token",
            Enabled = true,
            SyncIntervalMinutes = 5,
        };

        var service = CreateService(RespondOkJson(), startupDefaults);

        var result = await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task BackgroundSync_WithEmptyTenantUrl_ReturnsFailure_NotException()
    {
        var startupDefaults = new NocturneRemoteConnectorConfiguration();
        var tenantConfig = new NocturneRemoteConnectorConfiguration { Enabled = true, SyncIntervalMinutes = 5 };

        var service = CreateService(RespondOkJson(), startupDefaults);

        var act = async () => await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeFalse();
    }

    /// <summary>
    ///     The terminal progress message belongs to the shared run wrapper. A rejected token returns
    ///     before any data is fetched, and that path still owes the tenant exactly one terminal
    ///     message — without it the connector's badge stays on "syncing" until the page is reloaded.
    /// </summary>
    [Theory]
    [InlineData(true, SyncPhase.Completed)]
    [InlineData(false, SyncPhase.Failed)]
    public async Task RequestedSync_ReportsExactlyOneTerminalMessage(
        bool tokenAccepted, SyncPhase expectedPhase)
    {
        var handler = tokenAccepted
            ? RespondOkJson()
            : new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized")
            });
        var service = CreateService(handler, new NocturneRemoteConnectorConfiguration());
        var config = new NocturneRemoteConnectorConfiguration
        {
            Url = "https://remote.nocturne.example.com",
            Token = "bearer-token",
        };

        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);

        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose], From = DateTime.UtcNow.AddHours(-1) },
            config, CancellationToken.None, reporter.Object);

        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(expectedPhase);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
