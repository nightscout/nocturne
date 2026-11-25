using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Glooko.Tests.Services;

public class GlookoTokenExpiryTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Session_ExpiresFiveMinutesBeforeItsTwentyFourHourLifetimeEnds()
    {
        var clock = new FakeTimeProvider(Instant);
        using var provider = await SignedIn(clock);

        provider.TokenExpiresAt.Should().Be(Instant.UtcDateTime.AddHours(24).AddMinutes(-5));
    }

    [Fact]
    public async Task Session_IsValidUntilTheExpiryAndExpiredAfterIt()
    {
        var clock = new FakeTimeProvider(Instant);
        using var provider = await SignedIn(clock);

        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        provider.IsTokenExpired.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    private static async Task<GlookoAuthTokenProvider> SignedIn(TimeProvider clock)
    {
        var tenant = ResolvedTenant.Accessor();

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provider = new GlookoAuthTokenProvider(
            new HttpClient(LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            tenant,
            NullLogger<GlookoAuthTokenProvider>.Instance,
            retryDelay.Object,
            clock);

        var token = await provider.GetValidTokenAsync(
            new GlookoConnectorConfiguration { Email = "someone@example.com", Password = "hunter2", UseV3Api = false, MaxRetryAttempts = 3 },
            CancellationToken.None);
        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        return provider;
    }

    private static FakeHttpHandler LoginHandler() => new FakeHttpHandler(_ =>
    {
        var response = FakeHttpHandler.Status(
            HttpStatusCode.OK, "{\"userLogin\":{\"glookoCode\":\"blue-duke-4165\"}}");
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie", $"{GlookoConstants.SessionCookieName}=session-abc; Path=/; HttpOnly");
        return response;
    });
}
