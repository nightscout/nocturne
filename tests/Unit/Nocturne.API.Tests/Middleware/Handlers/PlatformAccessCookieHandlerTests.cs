using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Verifies that <see cref="PlatformAccessCookieHandler"/> only confers platform-access
/// authentication for a genuine, platform-access-marked grant pinned to the resolved tenant —
/// and skips (falls through to the normal auth chain) for anything else.
/// </summary>
public class PlatformAccessCookieHandlerTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private readonly IJwtService _jwt;
    private readonly PlatformAccessCookieHandler _handler;
    private readonly OidcOptions _oidc = new();

    public PlatformAccessCookieHandlerTests()
    {
        var jwtOptions = Options.Create(new JwtOptions
        {
            SecretKey = "platform-access-test-secret-key-32+chars",
            Issuer = "nocturne",
            Audience = "nocturne-api",
            AccessTokenLifetimeMinutes = 15,
        });
        _jwt = new JwtService(jwtOptions, NullLogger<JwtService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_jwt);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _handler = new PlatformAccessCookieHandler(
            scopeFactory,
            NullLogger<PlatformAccessCookieHandler>.Instance,
            Options.Create(_oidc));
    }

    [Fact]
    public async Task ValidGrant_MatchingTenant_AuthenticatesAsPlatformAccess()
    {
        var token = MintGrant(tenantId: _tenantId, platformAccess: true);
        var context = BuildContext(_tenantId, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue();
        result.AuthContext!.AuthType.Should().Be(AuthType.PlatformAccess);
        result.AuthContext.SubjectId.Should().Be(_subjectId);
        result.AuthContext.Permissions.Should().Contain("*");
    }

    [Fact]
    public async Task Grant_PinnedToDifferentTenant_Skips()
    {
        // Genuine platform-access grant, but for another tenant than the one resolved.
        var token = MintGrant(tenantId: _otherTenantId, platformAccess: true);
        var context = BuildContext(_tenantId, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TenantPinnedToken_WithoutPlatformAccessMarker_Skips()
    {
        // Escalation guard: an ordinary tenant-pinned token (e.g. an OAuth token) moved into
        // the platform-access cookie must NOT confer god mode.
        var token = MintGrant(tenantId: _tenantId, platformAccess: false);
        var context = BuildContext(_tenantId, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
    }

    [Fact]
    public async Task NoCookie_Skips()
    {
        var context = BuildContext(_tenantId, cookieValue: null);

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
    }

    [Fact]
    public async Task NoResolvedTenant_Skips()
    {
        var token = MintGrant(tenantId: _tenantId, platformAccess: true);
        var context = BuildContext(tenant: null, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidToken_Skips()
    {
        var context = BuildContext(_tenantId, cookieValue: "not-a-real-jwt");

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
    }

    private string MintGrant(Guid tenantId, bool platformAccess) =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Platform Operator", Email = "ops@example.com" },
            permissions: ["*"],
            roles: [],
            scopes: [],
            clientId: null,
            limitTo24Hours: false,
            tenantId: tenantId,
            lifetime: TimeSpan.FromMinutes(30),
            platformAccess: platformAccess);

    private DefaultHttpContext BuildContext(Guid? tenant, string? cookieValue)
    {
        var context = new DefaultHttpContext();
        if (cookieValue is not null)
        {
            context.Request.Headers["Cookie"] =
                $"{_oidc.Cookie.PlatformAccessName}={cookieValue}";
        }
        if (tenant is not null)
        {
            context.Items["TenantContext"] =
                new TenantContext(tenant.Value, "acme", "Acme", IsActive: true);
        }
        return context;
    }
}
