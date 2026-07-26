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

    [Fact]
    public async Task OwnerMember_WithNarrowlyScopedOAuthToken_KeepsOnlyTheTokenScopes()
    {
        // A tenant owner who authorized a third-party app for glucose.read only. The owner's
        // membership grants "*", but the access token is the consent boundary: widening to
        // superuser here would hand the app write/delete on every resource.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = SeedMemberWithRole(
            options, TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]);

        var (context, provider) = BuildMemberContext(options, subjectId, [OAuthScopes.GlucoseRead]);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain(OAuthScopes.GlucoseRead);
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain(OAuthScopes.TreatmentsReadWrite);
        grantedScopes.Should().NotContain(OAuthScopes.TherapyReadWrite);

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:entries:create").Should().BeFalse();
        permissionTrie.Check("api:profile:update").Should().BeFalse();
        permissionTrie.Check("*").Should().BeFalse();
    }

    [Fact]
    public async Task OwnerMember_WithFullAccessOAuthToken_StillGetsSuperuser()
    {
        // The same owner consenting to full access keeps superuser: the credential's own scope
        // list is what bounds it, and "*" bounds nothing.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = SeedMemberWithRole(
            options, TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]);

        var (context, provider) = BuildMemberContext(options, subjectId, [OAuthScopes.FullAccess]);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task OwnerMember_WithNarrowlyScopedDirectGrant_KeepsOnlyTheTokenScopes()
    {
        // Same token, presented as a bearer/?token= direct grant instead of an OAuth JWT. It must
        // resolve to the same narrow scopes it does in the api-secret header (AuthType.ApiKey).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = SeedMemberWithRole(
            options, TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]);

        var (context, provider) = BuildMemberContext(
            options, subjectId, [OAuthScopes.GlucoseRead], AuthType.DirectGrant);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);
    }

    [Fact]
    public async Task NonOwnerMember_WithDeviceScopedToken_RetainsDeviceScopes()
    {
        // A caretaker running the desktop Companion: the OAuth token carries the device
        // capability scopes, and the caretaker seed role grants the matching permission atoms,
        // so the scope intersection must keep them (they'd otherwise 403 on
        // POST /api/v4/client-devices).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = SeedMemberWithRole(
            options, TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Caretaker]);

        var (context, provider) = BuildMemberContext(options, subjectId, CompanionTokenScopes);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain(OAuthScopes.DeviceNotify);
        grantedScopes.Should().Contain(OAuthScopes.DeviceActuate);
        // Role atoms the token didn't request stay excluded.
        grantedScopes.Should().NotContain(OAuthScopes.TreatmentsReadWrite);
        grantedScopes.Should().NotContain("*");
    }

    [Fact]
    public async Task StaleSeedRole_WithoutDeviceAtoms_StillGrantsDeviceScopesFromToken()
    {
        // A tenant seeded before device.notify/device.actuate existed: its persisted caretaker
        // role row lacks the atoms and SeedRolesForTenantAsync never reconciles existing slugs.
        // The scopes are member-personal (the member's own client devices, not patient data), so
        // the middleware must grant them from the token alone — otherwise every pre-existing
        // tenant's members 403 on the client-devices API forever.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var staleCaretakerPermissions = TenantPermissions
            .SeedRolePermissions[TenantPermissions.SeedRoles.Caretaker]
            .Where(p => !TenantPermissions.MemberPersonalScopes.Contains(p))
            .ToList();
        var subjectId = SeedMemberWithRole(options, staleCaretakerPermissions);

        var (context, provider) = BuildMemberContext(options, subjectId, CompanionTokenScopes);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().Contain(OAuthScopes.DeviceNotify);
        grantedScopes.Should().Contain(OAuthScopes.DeviceActuate);
        // The role intersection still applies to everything else.
        grantedScopes.Should().Contain(OAuthScopes.GlucoseRead);
        grantedScopes.Should().NotContain(OAuthScopes.TreatmentsReadWrite);
        grantedScopes.Should().NotContain("*");
    }

    [Fact]
    public async Task ZeroPermissionMember_WithDeviceScopedToken_DoesNotGetDeviceScopes()
    {
        // The Denied seed role grants nothing. Device scopes must not bypass that: alert
        // actuations reveal patient state, so a member with no permissions at all gets none.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = SeedMemberWithRole(
            options, TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Denied]);

        var (context, provider) = BuildMemberContext(options, subjectId, CompanionTokenScopes);
        using (provider)
        {
            var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
            await middleware.InvokeAsync(context);
        }

        var grantedScopes = context.Items["GrantedScopes"] as IReadOnlySet<string>;
        grantedScopes.Should().NotBeNull();
        grantedScopes.Should().BeEmpty();
    }

    /// <summary>The desktop Companion's device-flow token scopes.</summary>
    private static readonly List<string> CompanionTokenScopes =
    [
        OAuthScopes.GlucoseRead, OAuthScopes.TherapyRead, OAuthScopes.DevicesRead,
        OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate,
    ];

    /// <summary>
    /// Seeds the default tenant plus a non-owner member holding a single role with the given
    /// permission atoms. Returns the member's subject id.
    /// </summary>
    private static Guid SeedMemberWithRole(
        DbContextOptions<NocturneDbContext> options, List<string> rolePermissions)
    {
        var subjectId = Guid.CreateVersion7();
        using var seed = new NocturneDbContext(options);
        seed.Database.EnsureCreated();
        TestDatabaseSeeder.Seed(seed);

        seed.Subjects.Add(new Nocturne.Infrastructure.Data.Entities.SubjectEntity
        {
            Id = subjectId,
            Name = "Member",
            IsActive = true,
            IsSystemSubject = false,
        });
        var memberId = Guid.CreateVersion7();
        seed.TenantMembers.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberEntity
        {
            Id = memberId,
            TenantId = TestDatabaseSeeder.TenantId,
            SubjectId = subjectId,
        });
        var roleId = Guid.CreateVersion7();
        seed.TenantRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantRoleEntity
        {
            Id = roleId,
            TenantId = TestDatabaseSeeder.TenantId,
            Name = "Member Role",
            Slug = "member-role",
            Permissions = rolePermissions,
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        seed.TenantMemberRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = roleId,
            SysCreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        return subjectId;
    }

    /// <summary>
    /// Builds an HTTP context for an OAuth-token member request whose token carries the given
    /// scopes, as AuthenticationMiddleware would leave it. The caller disposes the provider.
    /// </summary>
    private static (DefaultHttpContext context, ServiceProvider provider) BuildMemberContext(
        DbContextOptions<NocturneDbContext> options,
        Guid subjectId,
        List<string> tokenScopes,
        AuthType authType = AuthType.OAuthAccessToken)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new NocturneDbContext(options));
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = subjectId,
            TenantId = TestDatabaseSeeder.TenantId,
            Scopes = tokenScopes,
        };
        context.Items["GrantedScopes"] = OAuthScopes.Normalize(tokenScopes);
        context.Items["PermissionTrie"] = new PermissionTrie();

        return (context, provider);
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
