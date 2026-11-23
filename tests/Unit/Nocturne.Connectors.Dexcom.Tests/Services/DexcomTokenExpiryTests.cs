using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Dexcom.Tests.Services;

public class DexcomTokenExpiryTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Session_ExpiresAnHourBeforeItsTwentyFourHourLifetimeEnds()
    {
        var clock = new FakeTimeProvider(Instant);
        using var provider = await SignedIn(clock);

        provider.TokenExpiresAt.Should().Be(Instant.UtcDateTime.AddHours(23));
    }

    [Fact]
    public async Task Session_IsValidUntilTheExpiryAndExpiredAfterIt()
    {
        var clock = new FakeTimeProvider(Instant);
        using var provider = await SignedIn(clock);

        clock.Advance(TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1));
        provider.IsTokenExpired.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    private static async Task<DexcomAuthTokenProvider> SignedIn(TimeProvider clock)
    {
        var tenant = ResolvedTenant.Accessor();

        var provider = new DexcomAuthTokenProvider(
            new HttpClient(LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<DexcomConnectorConfiguration>(
                new Dictionary<string, string> { ["US"] = DexcomConstants.Servers.Us },
                config => ((DexcomConnectorConfiguration)config).Server,
                null),
            tenant,
            NullLogger<DexcomAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);

        var token = await provider.GetValidTokenAsync(
            new DexcomConnectorConfiguration { Username = "someone@example.com", Password = "hunter2", Server = "US" },
            CancellationToken.None);
        token.Should().Be("session-id-1");
        return provider;
    }

    private static FakeHttpHandler LoginHandler() => new FakeHttpHandler(
            _ => FakeHttpHandler.Status(HttpStatusCode.OK, "\"session-id-1\""))
        .On("AuthenticatePublisherAccount", FakeHttpHandler.Status(HttpStatusCode.OK, "\"account-id-1\""));
}
