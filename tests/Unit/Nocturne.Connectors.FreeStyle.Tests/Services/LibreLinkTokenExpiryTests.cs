using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.FreeStyle.Tests.Services;

public class LibreLinkTokenExpiryTests
{
    [Fact]
    public async Task Token_ExpiresAnHourBeforeItsTwentyFourHourLifetimeEnds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var provider = ProviderFor(clock);

        var token = await provider.GetValidTokenAsync(
            new LibreLinkUpConnectorConfiguration { Username = "someone@example.com", Password = "hunter2" },
            CancellationToken.None);

        token.Should().Be("ticket-1");
        provider.TokenExpiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddHours(23));
        clock.Advance(TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1));
        provider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    private static LibreLinkAuthTokenProvider ProviderFor(TimeProvider clock)
    {
        var tenant = ResolvedTenant.Accessor();

        return new LibreLinkAuthTokenProvider(
            new HttpClient(LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(
                null, null, LibreLinkUpConstants.Endpoints.Eu),
            tenant,
            NullLogger<LibreLinkAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);
    }

    private static FakeHttpHandler LoginHandler() => new FakeHttpHandler(_ => FakeHttpHandler.Status(
        HttpStatusCode.OK, "{\"status\":0,\"data\":{\"authTicket\":{\"token\":\"ticket-1\"}}}"));
}
