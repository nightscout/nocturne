using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Round-trip tests: tokens are minted with the real <see cref="JwtService"/> (as the
/// token-exchange endpoint does) and presented back to <see cref="LegacyJwtHandler"/>.
/// Literal-name claim lookups ("sub", "roles", "permissions") pass a hand-rolled unit
/// test but fail on real tokens, because JwtSecurityTokenHandler's inbound claim-type
/// map rewrites sub/role during validation — so these tests must use the real service
/// on both sides.
/// </summary>
public class LegacyJwtHandlerTests
{
    private readonly JwtService _jwtService;
    private readonly LegacyJwtHandler _handler;

    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public LegacyJwtHandlerTests()
    {
        _jwtService = CreateJwtService("test-secret-key-with-at-least-32-characters!");

        var services = new ServiceCollection();
        services.AddScoped<IJwtService>(_ => _jwtService);
        var serviceProvider = services.BuildServiceProvider();

        _handler = new LegacyJwtHandler(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<LegacyJwtHandler>>());
    }

    private static JwtService CreateJwtService(string secretKey)
    {
        return new JwtService(
            Options.Create(new JwtOptions { SecretKey = secretKey }),
            Mock.Of<ILogger<JwtService>>());
    }

    private static DefaultHttpContext CreateHttpContext(string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        if (bearerToken != null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }
        return context;
    }

    private SubjectInfo TestSubject => new()
    {
        Id = _subjectId,
        Name = "aaps-uploader",
    };

    [Fact]
    public async Task AuthenticateAsync_ExchangeMintedJwt_ParsesSubjectRolesAndPermissions()
    {
        // The exact token shape minted by /api/v2/authorization/request/{accessToken}:
        // permissions + roles, no OAuth scope/client_id claims.
        var token = _jwtService.GenerateAccessToken(
            TestSubject,
            permissions: ["api:treatments:read", "api:entries:read"],
            roles: ["readable", "careportal"]);

        var result = await _handler.AuthenticateAsync(CreateHttpContext(token));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.LegacyJwt, result.AuthContext!.AuthType);
        Assert.Equal(_subjectId, result.AuthContext.SubjectId);
        Assert.Equal("aaps-uploader", result.AuthContext.SubjectName);
        Assert.Equal(["readable", "careportal"], result.AuthContext.Roles);
        Assert.Equal(["api:treatments:read", "api:entries:read"], result.AuthContext.Permissions);
    }

    [Fact]
    public async Task AuthenticateAsync_ScopedJwtWithoutOAuthClaims_ParsesScopes()
    {
        // A tenant-pinned scoped token with no scopes granted carries neither scope nor
        // client_id, so OAuthAccessTokenHandler skips it and it lands here.
        var token = _jwtService.GenerateAccessToken(
            TestSubject,
            permissions: [],
            roles: ["readable"],
            scopes: []);

        var result = await _handler.AuthenticateAsync(CreateHttpContext(token));

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
        Assert.Empty(result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_NoAuthorizationHeader_Skips()
    {
        var result = await _handler.AuthenticateAsync(CreateHttpContext());

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_OpaqueBearerToken_Skips()
    {
        var result = await _handler.AuthenticateAsync(CreateHttpContext("noc_opaquetokenvalue"));

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredToken_Fails()
    {
        // JwtService refuses to mint an already-expired token (Expires must be after
        // NotBefore), so craft one directly with the same key, issuer, and audience.
        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("sub", _subjectId.ToString())]),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            Issuer = "nocturne",
            Audience = "nocturne-api",
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes("test-secret-key-with-at-least-32-characters!")),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256Signature),
        };
        var token = tokenHandler.WriteToken(tokenHandler.CreateToken(descriptor));

        var result = await _handler.AuthenticateAsync(CreateHttpContext(token));

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongSigningKey_Fails()
    {
        var foreignService = CreateJwtService("another-secret-key-with-32-characters!!");
        var token = foreignService.GenerateAccessToken(
            TestSubject,
            permissions: [],
            roles: ["readable"]);

        var result = await _handler.AuthenticateAsync(CreateHttpContext(token));

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_TenantPinnedToken_WrongTenant_Fails()
    {
        var token = _jwtService.GenerateAccessToken(
            TestSubject,
            permissions: [],
            roles: [],
            scopes: [],
            tenantId: _tenantId);

        var context = CreateHttpContext(token);
        context.Items["TenantContext"] = new TenantContext(
            Guid.CreateVersion7(), "other", "Other", true);

        var result = await _handler.AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_TenantPinnedToken_MatchingTenant_Succeeds()
    {
        var token = _jwtService.GenerateAccessToken(
            TestSubject,
            permissions: [],
            roles: [],
            scopes: [],
            tenantId: _tenantId);

        var context = CreateHttpContext(token);
        context.Items["TenantContext"] = new TenantContext(_tenantId, "default", "Default", true);

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
    }

    [Fact]
    public void Priority_Is200()
    {
        Assert.Equal(200, _handler.Priority);
    }

    [Fact]
    public void Name_IsLegacyJwtHandler()
    {
        Assert.Equal("LegacyJwtHandler", _handler.Name);
    }
}
