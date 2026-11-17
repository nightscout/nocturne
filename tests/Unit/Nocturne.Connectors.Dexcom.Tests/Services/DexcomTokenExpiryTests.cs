using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

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
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var provider = new DexcomAuthTokenProvider(
            new HttpClient(new LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<DexcomConnectorConfiguration>(
                new Dictionary<string, string> { ["US"] = DexcomConstants.Servers.Us },
                config => ((DexcomConnectorConfiguration)config).Server,
                null),
            tenant.Object,
            NullLogger<DexcomAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);

        var token = await provider.GetValidTokenAsync(
            new DexcomConnectorConfiguration { Username = "someone@example.com", Password = "hunter2", Server = "US" },
            CancellationToken.None);
        token.Should().Be("session-id-1");
        return provider;
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.ToString().EndsWith("AuthenticatePublisherAccount", StringComparison.Ordinal)
                ? "\"account-id-1\""
                : "\"session-id-1\"";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
