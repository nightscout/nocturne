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
using Nocturne.API.Extensions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
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
            _dbContext.AsFactory(), new OAuthGrantRepository(_dbContext.AsFactory()),
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())),
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            TimeProvider.System,
            NullLogger<GuestLinkService>.Instance);

        var handler = new GuestSessionHandler(
            new EphemeralDataProtectionProvider(),
            Mock.Of<IServiceScopeFactory>(),
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())),
            TimeProvider.System,
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
    public void CreateGuestLink_WithoutSharingGuest_ForbidsEvenSelfOwned()
    {
        ActionGate.Evaluate(
                typeof(GuestLinkController),
                nameof(GuestLinkController.CreateGuestLink),
                new HashSet<string>(StringComparer.Ordinal) { Scope.GlucoseRead },
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = _subjectId })
            .ShouldBeScopeRefusal(Scope.SharingGuest);
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

    private void BuildMixedContext(GrantCeiling? ceiling)
    {
        BuildContext(new HashSet<string>(StringComparer.Ordinal) { Scope.GlucoseRead, Scope.TreatmentsRead, Scope.SharingGuest });
        _controller.HttpContext.SetGrantCeiling(ceiling);
    }

    [Fact]
    public async Task CreateGuestLink_ByAMixedMember_RefusesACategoryOnlyTheTeamGives()
    {
        BuildMixedContext(new GrantCeiling(new HashSet<string> { Scope.GlucoseRead, Scope.SharingGuest }, null, false));

        var result = await _controller.CreateGuestLink(
            new CreateGuestLinkRequest("Team only", [Scope.TreatmentsRead]), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await _dbContext.OAuthGrants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        _controller.GetGuestLinkScopeOptions().Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<GuestLinkScopeOptions>().Which.Available.Should().NotContain(Scope.TreatmentsRead);
    }

    [Fact]
    public async Task CreateGuestLink_ByAMixedMember_GivesTheDirectReasonsLimitAndEnd()
    {
        var directEnd = DateTime.UtcNow.AddDays(2);
        BuildMixedContext(new GrantCeiling(new HashSet<string> { Scope.GlucoseRead, Scope.SharingGuest }, directEnd, true));

        var result = await _controller.CreateGuestLink(
            new CreateGuestLinkRequest("Direct", [Scope.GlucoseRead]), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var grant = await _dbContext.OAuthGrants.IgnoreQueryFilters().SingleAsync();
        grant.LimitTo24Hours.Should().BeTrue();
        grant.ExpiresAt.Should().NotBeNull().And.BeOnOrBefore(directEnd);
    }

    [Fact]
    public async Task CreateGuestLink_ByATeamOnlyMember_GivesNothing()
    {
        BuildMixedContext(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await _controller.CreateGuestLink(
            new CreateGuestLinkRequest("Team", [Scope.GlucoseRead]), CancellationToken.None));
        (await _dbContext.OAuthGrants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        _controller.GetGuestLinkScopeOptions().Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<GuestLinkScopeOptions>().Which.Available.Should().BeEmpty();
    }

    public void Dispose() => _dbContext.Dispose();
}
