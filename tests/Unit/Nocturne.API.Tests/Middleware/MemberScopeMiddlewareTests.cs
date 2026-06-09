using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
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
            Scopes = ["glucose.read"],
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert: should NOT have superuser wildcard
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("glucose.read");
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
    public async Task PlatformAccess_AlwaysGetsSuperuserAccess()
    {
        // A platform-admin tenant-access grant (verified + tenant-pinned by
        // PlatformAccessCookieHandler) gets full superuser on the granted tenant,
        // with no membership lookup.
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.PlatformAccess,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = [],
        });

        await middleware.InvokeAsync(context);

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
            Scopes = ["glucose.read", "treatments.readwrite"],
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("glucose.read");
        grantedScopes.Should().Contain("treatments.readwrite");
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain("therapy.read");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeTrue();
        permissionTrie.Check("api:profile:read").Should().BeFalse();
    }

    [Fact]
    public async Task TenantMemberWithWildcardRole_GetsSuperuserPermissionTrie()
    {
        // Arrange — a tenant owner whose membership role grants "*", but whose session token
        // carries no permissions. Session tokens are minted from the subject's GLOBAL roles,
        // which are empty for a normal owner (their access comes from tenant membership), so
        // AuthenticationMiddleware leaves an empty PermissionTrie. The superuser branch must
        // rebuild it, or HasPermissions-gated endpoints (the legacy v1 API, e.g. the realtime
        // /api/v1/entries probe) would 403 for the owner on their own tenant.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        using (var seed = new NocturneDbContext(options))
        {
            seed.Database.EnsureCreated();
            // Seeds the default tenant with TestSubjectId as owner (the "*" wildcard role).
            TestDatabaseSeeder.Seed(seed);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new NocturneDbContext(options));
        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = TestDatabaseSeeder.TestSubjectId,
            TenantId = TestDatabaseSeeder.TenantId,
            Permissions = [], // session JWT carries no permissions
        };
        // As AuthenticationMiddleware would set it for a token with no permissions.
        context.Items["PermissionTrie"] = new PermissionTrie();
        context.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>();

        var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — a non-empty wildcard trie so the HasPermissions policy succeeds.
        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.IsEmpty.Should().BeFalse();
        permissionTrie.Check("*").Should().BeTrue();

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes!.Should().Contain("*");
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
