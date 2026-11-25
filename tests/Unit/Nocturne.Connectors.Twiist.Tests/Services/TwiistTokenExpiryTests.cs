using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

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
        var tenant = ResolvedTenant.Accessor();

        return new TwiistAuthTokenProvider(
            new HttpClient(LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
            tenant,
            NullLogger<TwiistAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);
    }

    private static FakeHttpHandler LoginHandler() => new FakeHttpHandler(_ => FakeHttpHandler.Status(
        HttpStatusCode.OK,
        "{\"AuthenticationResult\":{\"AccessToken\":\"access-token-1\",\"RefreshToken\":\"refresh-token-1\"}}"));
}
