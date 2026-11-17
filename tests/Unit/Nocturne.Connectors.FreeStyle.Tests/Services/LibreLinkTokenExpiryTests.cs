using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

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
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        return new LibreLinkAuthTokenProvider(
            new HttpClient(new LoginHandler()),
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(
                null, null, LibreLinkUpConstants.Endpoints.Eu),
            tenant.Object,
            NullLogger<LibreLinkAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            clock);
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":0,\"data\":{\"authTicket\":{\"token\":\"ticket-1\"}}}")
            });
    }
}
