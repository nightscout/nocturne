using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Tests that verify authentication handler priority ordering.
///
/// Regression context: InstanceKeyHandler must NOT run before SessionCookieHandler.
/// SvelteKit sends both the X-Instance-Key header (for service auth) AND the user's
/// session cookies on every request. If the Instance Key handler runs first, it
/// authenticates the request as "instance-service" and the session cookie handler
/// never gets a chance to identify the actual user.
/// </summary>
[Trait("Category", "Unit")]
public class AuthHandlerPriorityTests
{
    [Fact]
    public void InstanceKeyHandler_ShouldRunAfter_SessionCookieHandler()
    {
        var instanceKeyHandler = new InstanceKeyHandler(
            new InstanceKeyValidator(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<InstanceKeyHandler>.Instance);

        var sessionCookieHandler = new SessionCookieHandler(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<SessionCookieHandler>.Instance,
            Options.Create(new OidcOptions()));

        instanceKeyHandler.Priority.Should().BeGreaterThan(
            sessionCookieHandler.Priority,
            "InstanceKeyHandler must run AFTER SessionCookieHandler so that " +
            "user session cookies take precedence over the service-level instance key");
    }

    [Fact]
    public void InstanceKeyHandler_ShouldRunBefore_OidcTokenHandler()
    {
        var instanceKeyHandler = new InstanceKeyHandler(
            new InstanceKeyValidator(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<InstanceKeyHandler>.Instance);

        instanceKeyHandler.Priority.Should().BeLessThan(100,
            "InstanceKeyHandler should run before OidcTokenHandler (priority 100) " +
            "so that infrastructure service calls are fast-pathed");
    }

    [Fact]
    public async Task AuthMiddleware_WhenBothHandlersSucceed_ShouldUseHigherPriorityHandler()
    {
        var sessionHandler = new StubAuthHandler(
            priority: 50,
            name: "SessionCookie",
            result: AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = Guid.NewGuid(),
                SubjectName = "real-user",
                Permissions = ["*"],
                Roles = ["admin"],
            }));

        var instanceKeyHandler = new StubAuthHandler(
            priority: 55,
            name: "InstanceKey",
            result: AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.InstanceKey,
                SubjectName = "instance-service",
                Permissions = ["*"],
                Roles = ["admin"],
            }));

        var publicAccessCacheService = new PublicAccessCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<IDbContextFactory<Nocturne.Infrastructure.Data.NocturneDbContext>>(),
            NullLogger<PublicAccessCacheService>.Instance);

        var scopeFactory = CreateScopeFactoryWithDb();

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [instanceKeyHandler, sessionHandler],
            environment: Mock.Of<IHostEnvironment>(e =>
                e.EnvironmentName == "Production"),
            publicAccessCacheService: publicAccessCacheService,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: scopeFactory);

        var httpContext = new DefaultHttpContext();
        await middleware.InvokeAsync(httpContext);

        var authContext = httpContext.Items["AuthContext"] as AuthContext;
        authContext.Should().NotBeNull();
        authContext!.SubjectName.Should().Be("real-user",
            "the handler with lower priority number (SessionCookie=50) should win " +
            "over the handler with higher priority number (InstanceKey=55)");
        authContext.AuthType.Should().Be(AuthType.SessionCookie);
    }

    [Fact]
    public async Task AuthMiddleware_WhenSessionCookieSkips_ShouldFallBackToInstanceKey()
    {
        var sessionHandler = new StubAuthHandler(
            priority: 50,
            name: "SessionCookie",
            result: AuthResult.Skip());

        var instanceKeyHandler = new StubAuthHandler(
            priority: 55,
            name: "InstanceKey",
            result: AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.InstanceKey,
                SubjectName = "instance-service",
                Permissions = ["*"],
                Roles = ["admin"],
            }));

        var publicAccessCacheService = new PublicAccessCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<IDbContextFactory<Nocturne.Infrastructure.Data.NocturneDbContext>>(),
            NullLogger<PublicAccessCacheService>.Instance);

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [instanceKeyHandler, sessionHandler],
            environment: Mock.Of<IHostEnvironment>(e =>
                e.EnvironmentName == "Production"),
            publicAccessCacheService: publicAccessCacheService,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: Mock.Of<IServiceScopeFactory>());

        var httpContext = new DefaultHttpContext();
        await middleware.InvokeAsync(httpContext);

        var authContext = httpContext.Items["AuthContext"] as AuthContext;
        authContext.Should().NotBeNull();
        authContext!.SubjectName.Should().Be("instance-service",
            "when the session cookie handler skips, the instance key handler should authenticate");
        authContext.AuthType.Should().Be(AuthType.InstanceKey);
    }

    [Fact]
    public async Task AuthMiddleware_WhenSessionCookieFails_ShouldNotFallBackToInstanceKey()
    {
        var sessionHandler = new StubAuthHandler(
            priority: 50,
            name: "SessionCookie",
            result: AuthResult.Failure("Invalid token"));

        var instanceKeyHandler = new StubAuthHandler(
            priority: 55,
            name: "InstanceKey",
            result: AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.InstanceKey,
                SubjectName = "instance-service",
                Permissions = ["*"],
                Roles = ["admin"],
            }));

        var publicAccessCacheService = new PublicAccessCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<IDbContextFactory<Nocturne.Infrastructure.Data.NocturneDbContext>>(),
            NullLogger<PublicAccessCacheService>.Instance);

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [instanceKeyHandler, sessionHandler],
            environment: Mock.Of<IHostEnvironment>(e =>
                e.EnvironmentName == "Production"),
            publicAccessCacheService: publicAccessCacheService,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: Mock.Of<IServiceScopeFactory>());

        var httpContext = new DefaultHttpContext();
        await middleware.InvokeAsync(httpContext);

        var authContext = httpContext.Items["AuthContext"] as AuthContext;
        authContext.Should().NotBeNull();
        authContext!.IsAuthenticated.Should().BeFalse(
            "when a handler explicitly fails (not skip), the chain should stop " +
            "and return unauthenticated rather than falling through to later handlers");
    }

    /// <summary>
    /// Creates a real IServiceScopeFactory backed by SQLite in-memory so the middleware
    /// can resolve NocturneDbContext when looking up platform admin flags.
    /// </summary>
    private static IServiceScopeFactory CreateScopeFactoryWithDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<NocturneDbContext>(options =>
            options.UseSqlite(connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        var sp = services.BuildServiceProvider();

        // Ensure schema is created
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.Database.EnsureCreated();

        return sp.GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class StubAuthHandler(int priority, string name, AuthResult result)
        : IAuthHandler
    {
        public int Priority => priority;
        public string Name => name;

        public Task<AuthResult> AuthenticateAsync(HttpContext context)
            => Task.FromResult(result);
    }
}
