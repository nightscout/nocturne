using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Services;

public class TidepoolConnectorServiceTests
{
    /// <summary>
    /// When authentication fails, the sync must report failure (so the connector surfaces as
    /// unhealthy) rather than silently returning a successful, empty result. Previously a missing
    /// token made the data fetches return null without error, so a bad-credential tenant was
    /// recorded as healthy and never alerted.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_ReportsFailure_WhenAuthenticationFails()
    {
        // Auth endpoint returns 401 → token provider yields no token.
        var authHandler = new StubHandler(HttpStatusCode.Unauthorized,
            "{\"code\":401,\"reason\":\"No user matched the given details\"}");
        using var authClient = new HttpClient(authHandler);
        using var dataClient = new HttpClient(); // never reached — auth fails first

        var resolver = new ConnectorServerResolver<TidepoolConnectorConfiguration>(null, null, "api.tidepool.org");

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        var rateLimiting = new Mock<IRateLimitingStrategy>();
        rateLimiting.Setup(r => r.ApplyDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var tokenProvider = new TidepoolAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            tenantAccessor.Object,
            NullLogger<TidepoolAuthTokenProvider>.Instance,
            retryDelay.Object);

        var service = new TidepoolConnectorService(
            dataClient,
            resolver,
            NullLogger<TidepoolConnectorService>.Instance,
            retryDelay.Object,
            rateLimiting.Object,
            tokenProvider);

        var config = new TidepoolConnectorConfiguration { Username = "wrong@example.com", Password = "nope" };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeFalse("an authentication failure must mark the sync unhealthy");
        result.Errors.Should().Contain(e => e.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
