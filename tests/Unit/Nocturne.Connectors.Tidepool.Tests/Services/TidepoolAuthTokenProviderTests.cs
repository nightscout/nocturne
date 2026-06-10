using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Services;

public class TidepoolAuthTokenProviderTests
{
    /// <summary>
    /// A non-retryable auth failure (e.g. a 401 from bad credentials) must fail after a single
    /// attempt. Retrying it 3× with multi-minute backoff wastes a connector concurrency slot and
    /// delays the connector — which is what previously let one bad-credential tenant stall others.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_OnNonRetryableAuthError()
    {
        var handler = new CountingHandler(
            HttpStatusCode.Unauthorized,
            "{\"code\":401,\"reason\":\"No user matched the given details\"}");
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var provider = new TidepoolAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TidepoolConnectorConfiguration>(null, null, "api.tidepool.org"),
            tenantAccessor.Object,
            NullLogger<TidepoolAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new TidepoolConnectorConfiguration { Username = "wrong@example.com", Password = "nope" };

        var token = await provider.GetValidTokenAsync(config, CancellationToken.None);

        token.Should().BeNull();
        handler.CallCount.Should().Be(1,
            "a non-retryable 401 must fail fast, not burn three attempts with backoff");
        retryDelay.Verify(r => r.ApplyRetryDelayAsync(It.IsAny<int>()), Times.Never,
            "no backoff delay should be applied for a non-retryable error");
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
        }
    }
}
