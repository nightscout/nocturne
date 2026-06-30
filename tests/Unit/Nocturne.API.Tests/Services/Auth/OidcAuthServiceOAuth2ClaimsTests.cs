using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Tests generic OAuth2 identity resolution in <see cref="OidcAuthService.FetchOAuth2UserClaimsAsync"/>.
/// OAuth2 providers issue no ID token, so identity is read from a configured userinfo endpoint and
/// mapped to standard claims via the provider's claim mappings; a separate email endpoint is used when
/// the profile response carries no email. No provider is special-cased — a GitHub-shaped provider is
/// just one configuration.
/// </summary>
public class OidcAuthServiceOAuth2ClaimsTests
{
    private readonly Mock<IHttpClientFactory> _httpFactory = new();
    private readonly OidcAuthService _service;
    private readonly RoutingHandler _handler = new();

    public OidcAuthServiceOAuth2ClaimsTests()
    {
        _httpFactory
            .Setup(f => f.CreateClient("OidcProvider"))
            .Returns(() => new HttpClient(_handler));

        _service = new OidcAuthService(
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<ISessionService>().Object,
            new Mock<IJwtService>().Object,
            new Mock<IRefreshTokenService>().Object,
            _httpFactory.Object,
            new Mock<ITenantMemberService>().Object,
            Options.Create(new OidcOptions()),
            new Mock<IConfiguration>().Object,
            NullLogger<OidcAuthService>.Instance);
    }

    private static OidcProvider ProviderWith(OAuth2ProviderSettings settings) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Acme",
        ProviderType = OidcProviderType.OAuth2,
        IssuerUrl = "https://acme.example.com",
        OAuth2 = settings,
    };

    private static OidcDiscoveryDocument DiscoveryFor(OAuth2ProviderSettings s) => new()
    {
        AuthorizationEndpoint = s.AuthorizationEndpoint,
        TokenEndpoint = s.TokenEndpoint,
        UserInfoEndpoint = s.UserInfoEndpoint,
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetchOAuth2UserClaimsAsync_MapsUserInfoFieldsViaClaimMappings()
    {
        // GitHub-shaped: numeric id, login, avatar_url — all mapped to standard claims by configuration.
        var settings = new OAuth2ProviderSettings
        {
            UserInfoEndpoint = "https://api.acme.example.com/user",
            ClaimMappings = new()
            {
                ["sub"] = "id",
                ["preferred_username"] = "login",
                ["name"] = "name",
                ["email"] = "email",
                ["picture"] = "avatar_url",
            },
        };
        _handler.OnGet("https://api.acme.example.com/user",
            """{ "id": 4321, "login": "octocat", "name": "Mona Octocat", "email": "mona@acme.example.com", "avatar_url": "https://cdn.acme/u/4321" }""");

        var claims = await _service.FetchOAuth2UserClaimsAsync(ProviderWith(settings), DiscoveryFor(settings), "tok");

        claims.Sub.Should().Be("4321", "a numeric subject identifier is stringified");
        claims.PreferredUsername.Should().Be("octocat");
        claims.Name.Should().Be("Mona Octocat");
        claims.Email.Should().Be("mona@acme.example.com");
        claims.Picture.Should().Be("https://cdn.acme/u/4321");
        claims.EmailVerified.Should().BeNull("an email read straight from userinfo carries no verification signal");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetchOAuth2UserClaimsAsync_WithoutMappings_FallsBackToStandardFieldNames()
    {
        var settings = new OAuth2ProviderSettings
        {
            UserInfoEndpoint = "https://api.acme.example.com/userinfo",
        };
        _handler.OnGet("https://api.acme.example.com/userinfo",
            """{ "sub": "abc-123", "email": "user@acme.example.com", "name": "Jane" }""");

        var claims = await _service.FetchOAuth2UserClaimsAsync(ProviderWith(settings), DiscoveryFor(settings), "tok");

        claims.Sub.Should().Be("abc-123");
        claims.Email.Should().Be("user@acme.example.com");
        claims.Name.Should().Be("Jane");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetchOAuth2UserClaimsAsync_WhenProfileEmailMissing_UsesEmailEndpoint()
    {
        var settings = new OAuth2ProviderSettings
        {
            UserInfoEndpoint = "https://api.acme.example.com/user",
            UserInfoEmailEndpoint = "https://api.acme.example.com/user/emails",
            ClaimMappings = new() { ["sub"] = "id" },
        };
        _handler.OnGet("https://api.acme.example.com/user", """{ "id": 99, "email": null }""");
        _handler.OnGet("https://api.acme.example.com/user/emails",
            """
            [
              { "email": "secondary@acme.example.com", "primary": false, "verified": true },
              { "email": "primary@acme.example.com", "primary": true, "verified": true },
              { "email": "unverified@acme.example.com", "primary": false, "verified": false }
            ]
            """);

        var claims = await _service.FetchOAuth2UserClaimsAsync(ProviderWith(settings), DiscoveryFor(settings), "tok");

        claims.Sub.Should().Be("99");
        claims.Email.Should().Be("primary@acme.example.com", "the primary verified address is preferred");
        claims.EmailVerified.Should().BeTrue("an address from the verified-email endpoint is verified");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetchOAuth2UserClaimsAsync_EmailEndpoint_ToleratesNonBooleanVerifiedFlags()
    {
        var settings = new OAuth2ProviderSettings
        {
            UserInfoEndpoint = "https://api.acme.example.com/user",
            UserInfoEmailEndpoint = "https://api.acme.example.com/user/emails",
            ClaimMappings = new() { ["sub"] = "id" },
        };
        _handler.OnGet("https://api.acme.example.com/user", """{ "id": 7, "email": null }""");
        // A provider that expresses the flags as strings/numbers rather than JSON booleans must not break login.
        _handler.OnGet("https://api.acme.example.com/user/emails",
            """[ { "email": "primary@acme.example.com", "primary": "true", "verified": "true" } ]""");

        var claims = await _service.FetchOAuth2UserClaimsAsync(ProviderWith(settings), DiscoveryFor(settings), "tok");

        claims.Email.Should().Be("primary@acme.example.com");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetchOAuth2UserClaimsAsync_WhenNoSubject_Throws()
    {
        var settings = new OAuth2ProviderSettings { UserInfoEndpoint = "https://api.acme.example.com/user" };
        _handler.OnGet("https://api.acme.example.com/user", """{ "name": "No Subject" }""");

        var act = () => _service.FetchOAuth2UserClaimsAsync(ProviderWith(settings), DiscoveryFor(settings), "tok");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Minimal stub handler that returns canned responses keyed by absolute request URL.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();

        public void OnGet(string url, string body = "", HttpStatusCode status = HttpStatusCode.OK)
            => _routes[url] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_routes.TryGetValue(url, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
