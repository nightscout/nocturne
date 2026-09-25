using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class GuestLinkControllerTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly GuestLinkController _controller;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public GuestLinkControllerTests()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new NocturneDbContext(options) { TenantId = _tenantId };
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });
        _dbContext.SaveChanges();

        var service = new GuestLinkService(
            _dbContext,
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())),
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            NullLogger<GuestLinkService>.Instance);

        var handler = new GuestSessionHandler(
            new EphemeralDataProtectionProvider(),
            Mock.Of<IServiceScopeFactory>(),
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GuestSessionHandler>.Instance);

        _controller = new GuestLinkController(service, handler);
    }

    private void BuildContext(IReadOnlySet<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
        };
        httpContext.Items["GrantedScopes"] = grantedScopes;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task CreateGuestLink_WithoutSharingGuest_ForbidsEvenSelfOwned()
    {
        BuildContext(new HashSet<string>(StringComparer.Ordinal) { Scope.GlucoseRead });

        var result = await _controller.CreateGuestLink(
            new CreateGuestLinkRequest("Self Owned"), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await _dbContext.OAuthGrants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateGuestLink_ExplicitScopeWiderThanCreator_Returns403WithCodeAndWritesNoGrant()
    {
        BuildContext(new HashSet<string>(StringComparer.Ordinal)
        {
            Scope.GlucoseRead, Scope.SharingGuest,
        });

        var result = await _controller.CreateGuestLink(
            new CreateGuestLinkRequest("Too Wide", [Scope.TreatmentsRead]), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Title.Should().Be(GrantCeilingViolation.ExceedsGranter);
        (await _dbContext.OAuthGrants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    public void Dispose() => _dbContext.Dispose();
}
