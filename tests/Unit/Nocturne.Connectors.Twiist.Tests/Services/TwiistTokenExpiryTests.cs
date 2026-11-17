using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Services;

public class TwiistTokenExpiryTests
{
    [Fact]
    public async Task Token_ExpiresFiveMinutesBeforeItsOneHourLifetimeEnds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var provider = ProviderFor(clock);

        var token = await provider.GetValidTokenAsync(
            new TwiistConnectorConfiguration { Username = "someone@example.com", Password = "hunter2" },
            CancellationToken.None);

        token.Should().Be("access-token-1");
        provider.TokenExpiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(55));
        clock.Advance(TimeSpan.FromMinutes(55) - TimeSpan.FromSeconds(1));
        provider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    private static TwiistAuthTokenProvider ProviderFor(TimeProvider clock)
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        return new TwiistAuthTokenProvider(
            new HttpClient(new LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
            tenant.Object,
            NullLogger<TwiistAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"AuthenticationResult\":{\"AccessToken\":\"access-token-1\",\"RefreshToken\":\"refresh-token-1\"}}")
            });
    }
}
