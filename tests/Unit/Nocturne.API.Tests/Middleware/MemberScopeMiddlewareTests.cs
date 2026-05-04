using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class MemberScopeMiddlewareTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    [Fact]
    public async Task ApiKey_WithScopedGrant_DoesNotGetSuperuserAccess()
    {
        // Arrange: API key with only entries.read scope
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = ["entries.read"],
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert: should NOT have superuser wildcard
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("entries.read");
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain("treatments.readwrite");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:read").Should().BeFalse();
        permissionTrie.Check("*").Should().BeFalse();
    }

    [Fact]
    public async Task ApiKey_WithFullAccessScope_GetsSuperuserAccess()
    {
        // Arrange: API key with full access
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = ["*"],
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert: full access normalizes to all scopes
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task InstanceKey_AlwaysGetsSuperuserAccess()
    {
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = [], // InstanceKey doesn't carry scopes
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert: always superuser regardless of scopes
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_WithMultipleScopes_GrantsOnlyThoseScopes()
    {
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = ["entries.read", "treatments.readwrite"],
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("entries.read");
        grantedScopes.Should().Contain("treatments.readwrite");
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain("profile.read");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeTrue();
        permissionTrie.Check("api:profile:read").Should().BeFalse();
    }

    private (MemberScopeMiddleware middleware, DefaultHttpContext context) Build(AuthContext authContext)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new MemberScopeMiddleware(next, NullLogger<MemberScopeMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Items["AuthContext"] = authContext;

        return (middleware, context);
    }
}
