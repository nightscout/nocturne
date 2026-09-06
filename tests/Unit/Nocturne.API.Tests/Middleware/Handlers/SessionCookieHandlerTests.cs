using System.Reflection;
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
    [InlineData("limit_24h")]
    public async Task AnyIndividualGrantClaim_IsNotAuthenticated(string claim)
    {
        var token = MintGrant(
            scopes: claim == "scope" ? ["glucose.read"] : [],
            clientId: claim == "client_id" ? "third-party-app" : null,
            tenantId: claim == "tenant_id" ? _tenantId : null,
            grantId: claim == "grant_id" ? Guid.CreateVersion7() : null,
            platformAccess: claim == "platform_access",
            limitTo24Hours: claim == "limit_24h");

        var result = await _handler.AuthenticateAsync(BuildContext(token));

        result.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The claims the session overload of <see cref="IJwtService.GenerateAccessToken"/> can emit
    /// (sub, name, email, roles, permission, jti, iat, exp). A session JWT carrying only these is
    /// the shape the handler accepts.
    /// </summary>
    private static readonly HashSet<string> SessionShapeClaims =
    [
        nameof(JwtClaims.SubjectId),
        nameof(JwtClaims.Name),
        nameof(JwtClaims.Email),
        nameof(JwtClaims.Roles),
        nameof(JwtClaims.Permissions),
        nameof(JwtClaims.JwtId),
        nameof(JwtClaims.IssuedAt),
        nameof(JwtClaims.ExpiresAt),
    ];

    [Fact]
    public void EveryJwtClaim_IsSessionShapedOrRejectedByTheGuard()
    {
        var guard = typeof(SessionCookieHandler).GetMethod(
            "IsGrantShaped",
            BindingFlags.NonPublic | BindingFlags.Static);

        guard.Should().NotBeNull(
            "this test drives SessionCookieHandler.IsGrantShaped directly; if it was renamed or "
            + "its signature changed, update the reflection lookup here");

        var uncovered = typeof(JwtClaims)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !SessionShapeClaims.Contains(p.Name))
            .Where(p =>
            {
                var claims = new JwtClaims();
                p.SetValue(claims, NonDefaultValueFor(p));
                return !(bool)guard!.Invoke(null, [claims])!;
            })
            .Select(p => p.Name)
            .ToList();

        uncovered.Should().BeEmpty(
            "every claim on JwtClaims must be explicitly triaged. Each of these is neither in "
            + "SessionShapeClaims (a claim the session overload of IJwtService.GenerateAccessToken "
            + "can emit) nor caught by SessionCookieHandler.IsGrantShaped. Decide which it is: if "
            + "the session overload emits it, add it to SessionShapeClaims; otherwise it is a "
            + "grant-only pin or ceiling, so add it to IsGrantShaped and add a row to "
            + "AnyIndividualGrantClaim_IsNotAuthenticated. Uncovered: {0}",
            string.Join(", ", uncovered));
    }

    private static object NonDefaultValueFor(PropertyInfo property) =>
        property.PropertyType switch
        {
            var t when t == typeof(Guid) || t == typeof(Guid?) => Guid.CreateVersion7(),
            var t when t == typeof(bool) || t == typeof(bool?) => true,
            var t when t == typeof(string) => "set",
            var t when t == typeof(List<string>) => new List<string> { "set" },
            var t when t == typeof(DateTimeOffset) || t == typeof(DateTimeOffset?) =>
                DateTimeOffset.UtcNow,
            _ => throw new NotSupportedException(
                $"JwtClaims.{property.Name} has type {property.PropertyType} which this test "
                + "cannot populate. Add a case here so the claim can be triaged."),
        };

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
        bool platformAccess = false,
        bool limitTo24Hours = false) =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Member", Email = "member@example.com" },
            permissions: ["api:read"],
            roles: ["reader"],
            scopes: scopes ?? [],
            clientId: clientId,
            limitTo24Hours: limitTo24Hours,
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
