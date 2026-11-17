using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Eversense.Tests.Services;

/// <summary>
///     Eversense's password-grant login runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential.
/// </summary>
public class EversenseAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new EversenseTokenHandler
        {
            FailFirstTokenRequestWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().Be("access-token-1");
        handler.TokenCalls.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenEversenseRejectsTheCredentialsWith400()
    {
        var handler = new EversenseTokenHandler { TokenStatus = HttpStatusCode.BadRequest };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.TokenCalls.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    /// <summary>
    ///     The configured MaxRetryAttempts is what the login loop spends.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task GetValidTokenAsync_SpendsExactlyMaxRetryAttempts_OnAPersistentRetryableError(
        int maxRetryAttempts)
    {
        var handler = new EversenseTokenHandler { TokenStatus = HttpStatusCode.ServiceUnavailable };

        var token = await AuthenticateAsync(handler, maxRetryAttempts);

        token.Should().BeNull();
        handler.TokenCalls.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    [Fact]
    public async Task Token_ExpiresFiveMinutesBeforeItsLifetimeEnds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var provider = ProviderFor(new EversenseTokenHandler(), clock);

        await provider.GetValidTokenAsync(Config(), CancellationToken.None);

        provider.TokenExpiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(55));
        clock.Advance(TimeSpan.FromMinutes(54));
        provider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromMinutes(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    private static EversenseAuthTokenProvider ProviderFor(EversenseTokenHandler handler, TimeProvider clock)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        return new EversenseAuthTokenProvider(
            new HttpClient(handler),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<EversenseConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            NullLogger<EversenseAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);
    }

    private static EversenseConnectorConfiguration Config() =>
        new() { Username = "someone@example.com", Password = "hunter2", Server = "US" };

    private static async Task<string?> AuthenticateAsync(
        EversenseTokenHandler handler, int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        using var provider = new EversenseAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<EversenseConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            NullLogger<EversenseAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new EversenseConnectorConfiguration
        {
            Username = "someone@example.com",
            Password = "hunter2",
            Server = "US",
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    /// <summary>Answers the token request of an Eversense login, with per-test failure injection.</summary>
    private sealed class EversenseTokenHandler : HttpMessageHandler
    {
        public HttpRequestException? FailFirstTokenRequestWith { get; init; }
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;

        public int TokenCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (!url.EndsWith(EversenseConstants.Endpoints.Token, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected Eversense request: {url}");

            TokenCalls++;
            if (FailFirstTokenRequestWith != null && TokenCalls == 1)
                throw FailFirstTokenRequestWith;

            return Task.FromResult(TokenStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"access-token-1\",\"expires_in\":3600}")
                }
                : new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\"}")
                });
        }
    }
}
