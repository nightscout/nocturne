using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Models.OAuth;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// A guest grant is listed by the OAuth grants API and by the guest-link API. Both show what the
/// grant authorizes, which for a stored <c>health.read</c> excludes <c>food.read</c>.
/// </summary>
[Trait("Category", "Unit")]
public class GuestLinkGrantListingTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _dataOwnerId = Guid.CreateVersion7();

    public GuestLinkGrantListingTests()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new NocturneDbContext(options);
        _dbContext.TenantId = _tenantId;

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _dataOwnerId,
            GrantType = OAuthGrantTypes.Guest,
            Scopes = [Scope.HealthRead],
            Label = "Clinic",
            TokenHash = "guesthash",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(48),
        });
        _dbContext.SaveChanges();
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task Both_listings_show_only_the_scopes_a_guest_can_hold()
    {
        var sessionCache = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));
        var revocation = new GrantRevocationService(sessionCache);

        var guestLinks = await new GuestLinkService(
                _dbContext.AsFactory(), new OAuthGrantRepository(_dbContext.AsFactory()), sessionCache, revocation, NullLogger<GuestLinkService>.Instance)
            .GetGuestLinksAsync(_dataOwnerId);

        var grantService = new OAuthGrantService(
            _dbContext.AsFactory(), new OAuthGrantRepository(_dbContext.AsFactory()),
            Mock.Of<IOAuthClientService>(),
            sessionCache,
            revocation,
            NullLogger<OAuthGrantService>.Instance);
        var result = await CreateController(grantService).GetGrants();
        var grants = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<OAuthGrantListResponse>().Subject.Grants;

        var listedAsGuestLink = guestLinks.Should().ContainSingle().Subject.Scopes;
        var listedAsGrant = grants.Should().ContainSingle().Subject.Scopes;

        listedAsGuestLink.Should().NotContain(Scope.FoodRead).And.Contain(Scope.GlucoseRead);
        listedAsGrant.Should().Equal(listedAsGuestLink);
    }

    private OAuthController CreateController(IOAuthGrantService grantService)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _dataOwnerId,
        };

        return new OAuthController(
            Mock.Of<IOAuthClientService>(),
            grantService,
            Mock.Of<IOAuthTokenService>(),
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
