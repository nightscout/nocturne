using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Verifies that <see cref="GuestSessionHandler"/> only authenticates a guest session on the
/// tenant its grant belongs to, and that a revoked grant stops authenticating without waiting
/// for the session cache to expire.
/// </summary>
public class GuestSessionHandlerTests
{
    private const string CookieName = "nocturne-guest-session";

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly Guid _dataOwnerId = Guid.CreateVersion7();

    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly GuestSessionCacheService _sessionCache;
    private readonly GuestSessionHandler _handler;

    public GuestSessionHandlerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using (var seed = new NocturneDbContext(_dbOptions))
        {
            seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "acme", DisplayName = "Acme" });
            seed.Tenants.Add(new TenantEntity { Id = _otherTenantId, Slug = "other", DisplayName = "Other" });
            seed.SaveChanges();
        }

        _sessionCache = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));

        // Mirrors the request pipeline: the handler sets the tenant on the scope's accessor and
        // the scoped DbContext is pinned from it, so the grant lookup is tenant-filtered.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_sessionCache);
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped(sp => new NocturneDbContext(_dbOptions)
        {
            TenantId = sp.GetRequiredService<ITenantAccessor>().TenantId,
        });
        services.AddScoped<IGuestLinkService, GuestLinkService>();
        var provider = services.BuildServiceProvider();

        _handler = new GuestSessionHandler(
            new EphemeralDataProtectionProvider(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            _sessionCache,
            NullLogger<GuestSessionHandler>.Instance);
    }

    [Fact]
    public async Task NoCookie_Skips()
    {
        var result = await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie: null));

        result.ShouldSkip.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ActivatedGrant_OnItsOwnTenant_Authenticates()
    {
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var cookie = ProtectCookie(grantId);

        var result = await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie));

        result.Succeeded.Should().BeTrue();
        result.AuthContext!.AuthType.Should().Be(AuthType.Guest);
        result.AuthContext.ActingAsSubjectId.Should().Be(_dataOwnerId);
    }

    /// <summary>
    /// A guest session authenticates with no subject of its own, so the audit trail has to name it
    /// by the grant it presented; a constant would put every guest of every tenant on one row key.
    /// </summary>
    [Fact]
    public async Task AuthenticatedGuest_IsIdentifiedByItsGrantInTheAuditTrail()
    {
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var otherGrantId = await SeedActivatedGrantAsync(_tenantId);

        var result = await _handler.AuthenticateAsync(BuildContext(_tenantId, ProtectCookie(grantId)));
        var other = await _handler.AuthenticateAsync(BuildContext(_tenantId, ProtectCookie(otherGrantId)));

        var actor = AuthAuditActor.FromCallerOtherThan(result.AuthContext, subjectId: null);
        var otherActor = AuthAuditActor.FromCallerOtherThan(other.AuthContext, subjectId: null);

        actor.Should().NotBeNull();
        actor!.Credential.Should().Be($"Guest:{grantId}");
        actor.SubjectId.Should().BeNull();
        otherActor!.Credential.Should().NotBe(actor.Credential);
    }

    [Fact]
    public async Task SessionWarmedOnOneTenant_IsRejectedOnAnother()
    {
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var cookie = ProtectCookie(grantId);

        // Warm the cache on the grant's own tenant, then replay the same cookie at another
        // tenant's host inside the cache TTL.
        (await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie))).Succeeded.Should().BeTrue();

        var result = await _handler.AuthenticateAsync(BuildContext(_otherTenantId, cookie));

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();

        // The legitimate tenant's session is unaffected.
        (await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task CachedSessionBelongingToAnotherTenant_IsRejected()
    {
        // Cached under the requested tenant's key but carrying another tenant's grant: the
        // handler's own comparison must reject it regardless of how it got into the cache.
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var cookie = ProtectCookie(grantId);
        _sessionCache.Set(_otherTenantId, grantId, new GuestSessionInfo(
            grantId,
            _tenantId,
            _dataOwnerId,
            [Scope.GlucoseRead],
            "Caregiver",
            DateTime.UtcNow.AddHours(1)));

        var result = await _handler.AuthenticateAsync(BuildContext(_otherTenantId, cookie));

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task NoResolvedTenant_IsRejected()
    {
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var cookie = ProtectCookie(grantId);

        var result = await _handler.AuthenticateAsync(BuildContext(tenantId: null, cookie));

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    [Fact]
    public async Task RevokedGrant_IsRejectedWithoutWaitingForCacheExpiry()
    {
        var grantId = await SeedActivatedGrantAsync(_tenantId);
        var cookie = ProtectCookie(grantId);

        (await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie))).Succeeded.Should().BeTrue();

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _tenantId })
        {
            var service = new GuestLinkService(ctx, _sessionCache, NullLogger<GuestLinkService>.Instance);
            (await service.RevokeAsync(grantId, _dataOwnerId)).Should().BeTrue();
        }

        var result = await _handler.AuthenticateAsync(BuildContext(_tenantId, cookie));

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }

    private async Task<Guid> SeedActivatedGrantAsync(Guid tenantId)
    {
        await using var ctx = new NocturneDbContext(_dbOptions) { TenantId = tenantId };
        var service = new GuestLinkService(ctx, _sessionCache, NullLogger<GuestLinkService>.Instance);

        var created = await service.CreateGuestLinkAsync(
            _dataOwnerId, _dataOwnerId, "Caregiver", "https://acme.example.test");
        var activation = await service.ActivateAsync(created.Code, "1.2.3.4", "TestAgent");

        activation.Success.Should().BeTrue();
        return activation.Session!.GrantId;
    }

    /// <summary>
    /// Produces a cookie value using the handler's own protector.
    /// </summary>
    private string ProtectCookie(Guid grantId)
    {
        var writer = new DefaultHttpContext();
        _handler.SetGuestSessionCookie(writer, grantId, DateTime.UtcNow.AddHours(1));

        var setCookie = writer.Response.Headers.SetCookie.ToString();
        return setCookie.Split(';')[0].Split('=', 2)[1];
    }

    private static DefaultHttpContext BuildContext(Guid? tenantId, string? cookie)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers["Cookie"] = $"{CookieName}={cookie}";
        }
        if (tenantId is not null)
        {
            context.Items["TenantContext"] =
                new TenantContext(tenantId.Value, "acme", "Acme", IsActive: true, IsDemo: false);
        }
        return context;
    }
}
