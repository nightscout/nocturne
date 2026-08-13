using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Verifies that <see cref="SessionCookieHandler"/> only accepts first-party, session-shaped
/// JWTs from the session access-token cookie. Grant-derived tokens (OAuth access tokens,
/// platform-access grants) are signed with the same key/issuer/audience, so a holder could
/// otherwise move one into the session cookie and shed its tenant pin and scope ceiling.
/// </summary>
public class SessionCookieHandlerTests
{
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();

    private readonly IJwtService _jwt;
    private readonly SessionCookieHandler _handler;
    private readonly OidcOptions _oidc = new();

    public SessionCookieHandlerTests()
    {
        var jwtOptions = Options.Create(new JwtOptions
        {
            SecretKey = "session-cookie-handler-test-secret-key-32+chars",
            Issuer = "nocturne",
            Audience = "nocturne-api",
            AccessTokenLifetimeMinutes = 15,
        });
        _jwt = new JwtService(jwtOptions, NullLogger<JwtService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_jwt);
        var provider = services.BuildServiceProvider();

        _handler = new SessionCookieHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionCookieHandler>.Instance,
            Options.Create(_oidc));
    }

    [Fact]
    public async Task SessionShapedToken_Authenticates()
    {
        // Regression guard: the shape a real login mints (SessionService / OidcAuthService both
        // use the overload with no scope, client_id, tenant_id, grant_id or platform_access).
        var token = _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Member", Email = "member@example.com" },
            permissions: ["api:read"],
            roles: ["reader"]);

        var result = await _handler.AuthenticateAsync(BuildContext(token));

        result.Succeeded.Should().BeTrue();
        result.AuthContext!.AuthType.Should().Be(AuthType.SessionCookie);
        result.AuthContext.SubjectId.Should().Be(_subjectId);
    }

    [Fact]
    public async Task TenantPinnedScopedOAuthToken_IsNotAuthenticated()
    {
        // The headline vector: a tenant-pinned, scope-limited OAuth access token moved into the
        // session cookie would authenticate as an unscoped session credential.
        var token = MintGrant(
            scopes: ["glucose.read"],
            clientId: "third-party-app",
            tenantId: _tenantId,
            grantId: Guid.CreateVersion7());

        var result = await _handler.AuthenticateAsync(BuildContext(token));

        result.Succeeded.Should().BeFalse();
        result.AuthContext.Should().BeNull();
    }

    [Fact]
    public async Task PlatformAccessGrant_IsNotAuthenticated()
    {
        var token = MintGrant(platformAccess: true);

        var result = await _handler.AuthenticateAsync(BuildContext(token));

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("client_id")]
    [InlineData("tenant_id")]
    [InlineData("grant_id")]
    [InlineData("platform_access")]
    public async Task AnyIndividualGrantClaim_IsNotAuthenticated(string claim)
    {
        var token = MintGrant(
            scopes: claim == "scope" ? ["glucose.read"] : [],
            clientId: claim == "client_id" ? "third-party-app" : null,
            tenantId: claim == "tenant_id" ? _tenantId : null,
            grantId: claim == "grant_id" ? Guid.CreateVersion7() : null,
            platformAccess: claim == "platform_access");

        var result = await _handler.AuthenticateAsync(BuildContext(token));

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task NoCookies_Skips()
    {
        var result = await _handler.AuthenticateAsync(BuildContext(null));

        result.ShouldSkip.Should().BeTrue();
    }

    private string MintGrant(
        IEnumerable<string>? scopes = null,
        string? clientId = null,
        Guid? tenantId = null,
        Guid? grantId = null,
        bool platformAccess = false) =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Member", Email = "member@example.com" },
            permissions: ["api:read"],
            roles: ["reader"],
            scopes: scopes ?? [],
            clientId: clientId,
            limitTo24Hours: false,
            tenantId: tenantId,
            lifetime: TimeSpan.FromMinutes(15),
            platformAccess: platformAccess,
            grantId: grantId);

    private DefaultHttpContext BuildContext(string? accessToken)
    {
        var context = new DefaultHttpContext();
        if (accessToken is not null)
        {
            context.Request.Headers["Cookie"] = $"{_oidc.Cookie.AccessTokenName}={accessToken}";
        }
        return context;
    }
}
