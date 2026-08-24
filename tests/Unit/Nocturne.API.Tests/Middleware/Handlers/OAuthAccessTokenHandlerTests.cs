using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Verifies that a scoped, tenant-pinned access token from <see cref="JwtService"/> — the shape
/// the CareLink desktop link code carries — authenticates as a Bearer token on the issuing
/// tenant, and only there. The desktop companion relies on this path instead of a dedicated
/// auth scheme.
/// </summary>
public class OAuthAccessTokenHandlerTests
{
    private const string SecretKey = "oauth-handler-test-secret-key-32+chars";

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private readonly IJwtService _jwt;
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly OAuthAccessTokenHandler _handler;

    public OAuthAccessTokenHandlerTests()
    {
        _jwt = new JwtService(
            Options.Create(new JwtOptions
            {
                SecretKey = SecretKey,
                Issuer = "nocturne",
                Audience = "nocturne-api",
                AccessTokenLifetimeMinutes = 15,
            }),
            NullLogger<JwtService>.Instance);

        var revocationCache = new Mock<IOAuthTokenRevocationCache>();
        revocationCache
            .Setup(c => c.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var services = new ServiceCollection();
        services.AddSingleton(_jwt);
        services.AddSingleton(revocationCache.Object);
        services.AddSingleton(_grantService.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _handler = new OAuthAccessTokenHandler(scopeFactory, NullLogger<OAuthAccessTokenHandler>.Instance);
    }

    private string MintDesktopStyleToken() =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Acme User" },
            permissions: [],
            roles: [],
            scopes: ["connectors:carelink:connect"],
            tenantId: _tenantId,
            lifetime: TimeSpan.FromMinutes(10));

    /// <summary>An app token as the OAuth token endpoint mints it: scoped, pinned, grant-bound.</summary>
    private string MintGrantBoundToken(Guid grantId) =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Acme User" },
            permissions: [],
            roles: [],
            scopes: [Scope.GlucoseRead],
            tenantId: _tenantId,
            grantId: grantId);

    /// <summary>JwtService refuses to mint already-expired tokens, so build one by hand.</summary>
    private string MintExpiredToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var token = new JwtSecurityToken(
            issuer: "nocturne",
            audience: "nocturne-api",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, _subjectId.ToString()),
                new Claim("scope", "connectors:carelink:connect"),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            expires: DateTime.UtcNow.AddMinutes(-10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static DefaultHttpContext Request(string token, TenantContext? tenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        if (tenant != null)
        {
            context.Items["TenantContext"] = tenant;
        }
        return context;
    }

    /// <summary>A request carrying the token on the query string as a SignalR client does.</summary>
    private static DefaultHttpContext QueryRequest(string path, string token, TenantContext tenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = QueryString.Create("access_token", token);
        context.Items["TenantContext"] = tenant;
        return context;
    }

    private TenantContext Tenant(Guid? id = null) =>
        new(id ?? _tenantId, "acme", "Acme", IsActive: true, IsDemo: false);

    [Fact]
    public async Task Accepts_a_scoped_tenant_pinned_token_on_the_issuing_tenant()
    {
        var context = Request(MintDesktopStyleToken(), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue(result.Error);
        result.AuthContext!.AuthType.Should().Be(AuthType.OAuthAccessToken);
        result.AuthContext.SubjectId.Should().Be(_subjectId);
        result.AuthContext.Scopes.Should().BeEquivalentTo(["connectors:carelink:connect"]);
        result.AuthContext.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_the_token_on_a_different_tenant()
    {
        var context = Request(MintDesktopStyleToken(), Tenant(Guid.CreateVersion7()));

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_the_token_when_no_tenant_is_resolved()
    {
        var context = Request(MintDesktopStyleToken(), tenant: null);

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_an_expired_token()
    {
        var context = Request(MintExpiredToken(), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task Accepts_a_grant_bound_token_while_its_grant_is_active()
    {
        var grantId = Guid.CreateVersion7();
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(grantId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var context = Request(MintGrantBoundToken(grantId), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task Rejects_a_grant_bound_token_once_its_grant_is_revoked()
    {
        // Disconnecting a connected app revokes the grant; the app's still-valid access token must
        // stop working on its next request rather than at natural expiry.
        var grantId = Guid.CreateVersion7();
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(grantId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = Request(MintGrantBoundToken(grantId), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task Skips_a_non_jwt_bearer_token()
    {
        var context = Request("noc_an-opaque-api-token", Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
    }

    [Theory]
    [InlineData("/hubs/data")]
    [InlineData("/hubs")]
    public async Task Accepts_an_access_token_query_parameter_on_a_hub_path(string path)
    {
        // A WebSocket or SSE upgrade cannot carry an Authorization header, so every SignalR client
        // puts the token in access_token. Without this the hub connection is anonymous and every
        // method HubAuthorizationFilter gates is denied.
        var context = QueryRequest(path, MintDesktopStyleToken(), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue(result.Error);
        result.AuthContext!.AuthType.Should().Be(AuthType.OAuthAccessToken);
        result.AuthContext.SubjectId.Should().Be(_subjectId);
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/hubsomething")]
    public async Task Ignores_an_access_token_query_parameter_off_a_hub_path(string path)
    {
        // A query-string credential lands in access logs and referrers, so it is honoured only where
        // the transport leaves no alternative.
        var context = QueryRequest(path, MintDesktopStyleToken(), Tenant());

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }
}
