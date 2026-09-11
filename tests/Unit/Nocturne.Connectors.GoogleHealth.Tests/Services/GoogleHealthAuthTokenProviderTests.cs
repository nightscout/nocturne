using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Models;
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

    [Fact]
    public async Task Malformed_cached_scopes_are_discarded_before_refreshing()
    {
        var tenantId = Guid.NewGuid();
        var cache = new ConnectorTokenCache();
        await cache.SetAsync("GoogleHealth", tenantId, new ConnectorSession(
            "access-token",
            DateTime.UtcNow.AddHours(1),
            new Dictionary<string, string>
            {
                ["RefreshToken"] = "refresh-token",
                ["Scopes"] = "not-json",
                ["AccessTokenExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            }));
        var provider = CreateProvider(new StubHandler(_ => Json("""
            {
              "access_token":"refreshed-access-token",
              "expires_in":3600,
              "token_type":"Bearer"
            }
            """)), cache, tenantId);

        await provider.SeedSessionAsync(new GoogleHealthTokenSession("refresh-token", ["scope-a"]));
        var token = await provider.GetValidTokenAsync(Configuration("refresh-token"));
        var session = await provider.GetCurrentSessionAsync();

        token.Should().Be("refreshed-access-token");
        session.Should().NotBeNull();
        session!.RefreshToken.Should().Be("refresh-token");
        session.Scopes.Should().Equal("scope-a");
    }

    private static GoogleHealthAuthTokenProvider CreateProvider(
        HttpMessageHandler handler,
        ConnectorTokenCache? cache = null,
        Guid? tenantId = null)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(accessor => accessor.IsResolved).Returns(true);
        tenantAccessor.SetupGet(accessor => accessor.TenantId).Returns(tenantId ?? Guid.NewGuid());
        return new GoogleHealthAuthTokenProvider(
            new HttpClient(handler),
            cache ?? new ConnectorTokenCache(),
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
