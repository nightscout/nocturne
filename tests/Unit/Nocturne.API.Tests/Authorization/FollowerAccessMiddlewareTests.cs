using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for FollowerAccessMiddleware covering header parsing,
/// authentication checks, grant validation, and scope restriction.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class FollowerAccessMiddlewareTests
{
    private readonly Mock<IOAuthGrantService> _mockGrantService;
    private readonly Mock<ISubjectService> _mockSubjectService;
    private readonly Mock<ILogger<FollowerAccessMiddleware>> _mockLogger;

    private readonly Guid _ownerSubjectId = Guid.CreateVersion7();
    private readonly Guid _followerSubjectId = Guid.CreateVersion7();

    public FollowerAccessMiddlewareTests()
    {
        _mockGrantService = new Mock<IOAuthGrantService>();
        _mockSubjectService = new Mock<ISubjectService>();
        _mockLogger = new Mock<ILogger<FollowerAccessMiddleware>>();
    }

    private FollowerAccessMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new FollowerAccessMiddleware(next, _mockLogger.Object);
    }

    private DefaultHttpContext CreateHttpContext(
        AuthContext? authContext = null,
        IReadOnlySet<string>? grantedScopes = null,
        PermissionTrie? permissionTrie = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockGrantService.Object);
        services.AddSingleton(_mockSubjectService.Object);
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };

        if (authContext != null)
        {
            context.Items["AuthContext"] = authContext;
        }

        if (grantedScopes != null)
        {
            context.Items["GrantedScopes"] = grantedScopes;
        }

        if (permissionTrie != null)
        {
            context.Items["PermissionTrie"] = permissionTrie;
        }

        return context;
    }

    private AuthContext CreateAuthenticatedContext(Guid? subjectId = null)
    {
        return new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = subjectId ?? _followerSubjectId,
            SubjectName = "Test Follower",
            Permissions = new List<string> { "*" },
        };
    }

    private OAuthGrantInfo CreateFollowerGrant(IEnumerable<string>? scopes = null)
    {
        return new OAuthGrantInfo
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = Guid.CreateVersion7(),
            ClientId = "nocturne://follower",
            SubjectId = _ownerSubjectId,
            GrantType = "follower",
            Scopes = (scopes ?? new[] { OAuthScopes.EntriesRead, OAuthScopes.TreatmentsRead }).ToList(),
            FollowerSubjectId = _followerSubjectId,
            FollowerName = "Test Follower",
            CreatedAt = DateTime.UtcNow,
        };
    }

    // ---------------------------------------------------------------
    // No X-Acting-As header
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_NoHeader_PassesThroughUnchanged()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext);

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        authContext.ActingAsSubjectId.Should().BeNull();
        authContext.IsActingAsFollower.Should().BeFalse();
        authContext.EffectiveSubjectId.Should().Be(_followerSubjectId);
    }

    [Fact]
    public async Task InvokeAsync_EmptyHeader_PassesThroughUnchanged()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext);
        context.Request.Headers["X-Acting-As"] = "";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        authContext.ActingAsSubjectId.Should().BeNull();
    }

    // ---------------------------------------------------------------
    // Unauthenticated request with header
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_Unauthenticated_Returns401()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateHttpContext(); // no auth context
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedContext_Returns401()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var authContext = AuthContext.Unauthenticated();
        var context = CreateHttpContext(authContext);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ---------------------------------------------------------------
    // Invalid GUID in header
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_InvalidGuid_Returns400()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext);
        context.Request.Headers["X-Acting-As"] = "not-a-guid";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ---------------------------------------------------------------
    // Acting as self
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ActingAsSelf_PassesThroughUnchanged()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var authContext = CreateAuthenticatedContext(_followerSubjectId);
        var context = CreateHttpContext(authContext);
        context.Request.Headers["X-Acting-As"] = _followerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        authContext.ActingAsSubjectId.Should().BeNull();
        authContext.IsActingAsFollower.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // Valid follower grant
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ValidGrant_SetsActingAsContext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var grant = CreateFollowerGrant();
        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync(grant);

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_ownerSubjectId))
            .ReturnsAsync(new Subject { Id = _ownerSubjectId, Name = "Data Owner" });

        var broadScopes = OAuthScopes.Normalize(new[] { OAuthScopes.FullAccess });
        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext, broadScopes);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        authContext.ActingAsSubjectId.Should().Be(_ownerSubjectId);
        authContext.ActingAsSubjectName.Should().Be("Data Owner");
        authContext.IsActingAsFollower.Should().BeTrue();
        authContext.EffectiveSubjectId.Should().Be(_ownerSubjectId);
    }

    [Fact]
    public async Task InvokeAsync_ValidGrant_RestrictsScopes()
    {
        AuthContext? capturedAuthContext = null;
        IReadOnlySet<string>? capturedScopes = null;
        PermissionTrie? capturedTrie = null;

        var middleware = CreateMiddleware(ctx =>
        {
            capturedAuthContext = ctx.Items["AuthContext"] as AuthContext;
            capturedScopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
            capturedTrie = ctx.Items["PermissionTrie"] as PermissionTrie;
            return Task.CompletedTask;
        });

        // Grant allows only entries.read and treatments.read
        var grant = CreateFollowerGrant(new[] { OAuthScopes.EntriesRead, OAuthScopes.TreatmentsRead });
        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync(grant);

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_ownerSubjectId))
            .ReturnsAsync(new Subject { Id = _ownerSubjectId, Name = "Owner" });

        // User has broad scopes (full access)
        var broadScopes = OAuthScopes.Normalize(new[] { OAuthScopes.FullAccess });
        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext, broadScopes);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        capturedScopes.Should().NotBeNull();
        capturedScopes.Should().Contain(OAuthScopes.EntriesRead);
        capturedScopes.Should().Contain(OAuthScopes.TreatmentsRead);
        capturedScopes.Should().NotContain(OAuthScopes.DeviceStatusRead);
        capturedScopes.Should().NotContain(OAuthScopes.ProfileRead);
        capturedScopes.Should().NotContain(OAuthScopes.FullAccess);

        // Permission trie should reflect restricted scopes
        capturedTrie.Should().NotBeNull();
        capturedTrie!.Check("api:entries:read").Should().BeTrue();
        capturedTrie.Check("api:treatments:read").Should().BeTrue();
        capturedTrie.Check("api:devicestatus:read").Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // No active grant
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_NoActiveGrant_Returns403()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync((OAuthGrantInfo?)null);

        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ---------------------------------------------------------------
    // Scope intersection: user has narrow scopes, grant has matching
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ScopeIntersection_UserNarrowGrantBroad_RestrictsToIntersection()
    {
        IReadOnlySet<string>? capturedScopes = null;

        var middleware = CreateMiddleware(ctx =>
        {
            capturedScopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
            return Task.CompletedTask;
        });

        // Grant allows entries.read, treatments.read, devicestatus.read
        var grant = CreateFollowerGrant(new[]
        {
            OAuthScopes.EntriesRead,
            OAuthScopes.TreatmentsRead,
            OAuthScopes.DeviceStatusRead,
        });
        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync(grant);

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_ownerSubjectId))
            .ReturnsAsync(new Subject { Id = _ownerSubjectId, Name = "Owner" });

        // User only has entries.read scope
        var userScopes = OAuthScopes.Normalize(new[] { OAuthScopes.EntriesRead });
        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext, userScopes);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        capturedScopes.Should().NotBeNull();
        capturedScopes.Should().Contain(OAuthScopes.EntriesRead);
        // User doesn't have treatments.read or devicestatus.read, so they shouldn't be in the intersection
        capturedScopes.Should().NotContain(OAuthScopes.TreatmentsRead);
        capturedScopes.Should().NotContain(OAuthScopes.DeviceStatusRead);
    }

    [Fact]
    public async Task InvokeAsync_ScopeIntersection_ReadWriteImpliesRead()
    {
        IReadOnlySet<string>? capturedScopes = null;

        var middleware = CreateMiddleware(ctx =>
        {
            capturedScopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
            return Task.CompletedTask;
        });

        // Grant allows only entries.read
        var grant = CreateFollowerGrant(new[] { OAuthScopes.EntriesRead });
        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync(grant);

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_ownerSubjectId))
            .ReturnsAsync(new Subject { Id = _ownerSubjectId, Name = "Owner" });

        // User has entries.readwrite (which implies entries.read via SatisfiesScope)
        var userScopes = OAuthScopes.Normalize(new[] { OAuthScopes.EntriesReadWrite });
        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext, userScopes);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        // entries.read should be in the intersection because
        // the user's readwrite satisfies the grant's read
        capturedScopes.Should().NotBeNull();
        capturedScopes.Should().Contain(OAuthScopes.EntriesRead);
    }

    [Fact]
    public async Task InvokeAsync_ValidGrant_OwnerSubjectNotFound_SetsNullName()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);

        var grant = CreateFollowerGrant();
        _mockGrantService
            .Setup(g => g.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId, default))
            .ReturnsAsync(grant);

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_ownerSubjectId))
            .ReturnsAsync((Subject?)null);

        var broadScopes = OAuthScopes.Normalize(new[] { OAuthScopes.FullAccess });
        var authContext = CreateAuthenticatedContext();
        var context = CreateHttpContext(authContext, broadScopes);
        context.Request.Headers["X-Acting-As"] = _ownerSubjectId.ToString();

        await middleware.InvokeAsync(context);

        authContext.ActingAsSubjectId.Should().Be(_ownerSubjectId);
        authContext.ActingAsSubjectName.Should().BeNull();
    }
}
