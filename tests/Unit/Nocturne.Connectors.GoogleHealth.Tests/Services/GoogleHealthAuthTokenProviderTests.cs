using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthAuthTokenProviderTests
{
    [Fact]
    public async Task Refreshes_once_and_reuses_the_tenant_session()
    {
        var calls = 0;
        var provider = CreateProvider(new StubHandler(_ =>
        {
            calls++;
            return Json("""
                {
                  "access_token":"access-token",
                  "refresh_token":"rotated-refresh-token",
                  "expires_in":3600,
                  "token_type":"Bearer",
                  "scope":"scope-a scope-b"
                }
                """);
        }));
        var configuration = Configuration("initial-refresh-token");

        var first = await provider.GetValidTokenAsync(configuration);
        var second = await provider.GetValidTokenAsync(configuration);
        var session = await provider.GetCurrentSessionAsync();

        first.Should().Be("access-token");
        second.Should().Be("access-token");
        calls.Should().Be(1);
        session.Should().NotBeNull();
        session!.RefreshToken.Should().Be("rotated-refresh-token");
        session.Scopes.Should().Equal("scope-a", "scope-b");
        session.AccessTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Preserves_the_actionable_refresh_error()
    {
        var provider = CreateProvider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
        }));

        var action = () => provider.GetValidTokenAsync(Configuration("revoked-refresh-token"));

        var exception = await action.Should().ThrowAsync<GoogleHealthException>();
        exception.Which.Message.Should().Be("reconnect_required");
        exception.Which.Stage.Should().Be("token_refresh");
    }

    private static GoogleHealthAuthTokenProvider CreateProvider(HttpMessageHandler handler)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(accessor => accessor.IsResolved).Returns(true);
        tenantAccessor.SetupGet(accessor => accessor.TenantId).Returns(Guid.NewGuid());
        return new GoogleHealthAuthTokenProvider(
            new HttpClient(handler),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            NullLogger<GoogleHealthAuthTokenProvider>.Instance);
    }

    private static GoogleHealthConnectorConfiguration Configuration(string refreshToken) => new()
    {
        ClientId = "client.apps.googleusercontent.com",
        ClientSecret = "client-secret",
        CallbackUrl = "https://example.test/google-health/callback",
        RefreshToken = refreshToken
    };

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
