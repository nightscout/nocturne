using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Verifies that <see cref="PlatformAccessCookieHandler"/> only confers platform-access
/// authentication for a genuine, platform-access-marked grant pinned to the resolved tenant,
/// whose subject is still a platform admin — and skips (falls through to the normal auth chain)
/// for anything else.
/// </summary>
public class PlatformAccessCookieHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _revokedSubjectId = Guid.CreateVersion7();

    private readonly SqliteConnection _connection;
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

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using (var ctx = new NocturneDbContext(dbOptions))
        {
            ctx.Database.EnsureCreated();
            ctx.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Operator", IsActive = true, IsPlatformAdmin = true });
            ctx.Subjects.Add(new SubjectEntity { Id = _revokedSubjectId, Name = "Ex-Operator", IsActive = true, IsPlatformAdmin = false });
            ctx.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddSingleton(_jwt);
        services.AddDbContext<NocturneDbContext>(o => o
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _handler = new PlatformAccessCookieHandler(
            scopeFactory,
            NullLogger<PlatformAccessCookieHandler>.Instance,
            Options.Create(_oidc));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ValidGrant_MatchingTenant_PlatformAdmin_AuthenticatesAsPlatformAccess()
    {
        var token = MintGrant(_subjectId, tenantId: _tenantId, platformAccess: true);
        var context = BuildContext(_tenantId, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue();
        result.AuthContext!.AuthType.Should().Be(AuthType.PlatformAccess);
        result.AuthContext.SubjectId.Should().Be(_subjectId);
        result.AuthContext.Permissions.Should().Contain("*");
    }

    [Fact]
    public async Task SubjectNoLongerPlatformAdmin_Skips()
    {
        // Grant is otherwise valid (marker + matching tenant) but the subject's platform-admin
        // flag has since been revoked — the live re-check must reject it.
        var token = MintGrant(_revokedSubjectId, tenantId: _tenantId, platformAccess: true);
        var context = BuildContext(_tenantId, cookieValue: token);

        var result = await _handler.AuthenticateAsync(context);

        result.ShouldSkip.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Grant_PinnedToDifferentTenant_Skips()
    {
        var token = MintGrant(_subjectId, tenantId: _otherTenantId, platformAccess: true);
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
        var token = MintGrant(_subjectId, tenantId: _tenantId, platformAccess: false);
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
        var token = MintGrant(_subjectId, tenantId: _tenantId, platformAccess: true);
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

    private string MintGrant(Guid subjectId, Guid tenantId, bool platformAccess) =>
        _jwt.GenerateAccessToken(
            new SubjectInfo { Id = subjectId, Name = "Platform Operator", Email = "ops@example.com" },
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
