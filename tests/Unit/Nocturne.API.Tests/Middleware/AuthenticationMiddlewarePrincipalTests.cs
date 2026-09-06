using Nocturne.API.Extensions;
using System.Security.Claims;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Verifies that a tenant-membership rejection clears <see cref="HttpContext.User"/> and not just
/// <see cref="HttpContext.Items"/>. <c>[Authorize]</c> reads the principal, so a populated
/// principal left behind after rejection authorizes the caller against every endpoint whose only
/// gate is <c>[Authorize]</c> — which is most of the V4 surface — against the tenant they were
/// just rejected from.
/// </summary>
/// <remarks>
/// The two cases are load-bearing as a pair, so do not delete or skip the member case. An
/// exception anywhere before the principal is built also lands in the catch that calls
/// SetUnauthenticated, which satisfies the non-member assertions for the wrong reason. The two
/// runs differ only in what the membership mock returns, so a fault that broke one would fail the
/// other. An earlier version of this file passed exactly that way.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthenticationMiddlewarePrincipalTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    /// <summary>
    /// Stub handler that authenticates unconditionally, standing in for any credential whose
    /// validation does not itself pin the tenant (a session cookie carries no tenant claim).
    /// </summary>
    private sealed class StubHandler(AuthContext context) : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Stub";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) =>
            Task.FromResult(AuthResult.Success(context));
    }

    /// <summary>
    /// Stub handler that rejects the credential, standing in for every chain rejection: a token
    /// pinned to another tenant, a revoked grant, or any credential presented on a share host.
    /// </summary>
    private sealed class RejectingHandler : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Rejecting";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) =>
            Task.FromResult(AuthResult.Failure("not valid for this tenant"));
    }

    /// <summary>
    /// A principal shaped like the one the framework's JwtBearer scheme builds. The framework
    /// authentication middleware runs at the HEAD of the pipeline (minimal hosting auto-inserts it
    /// because AddAuthentication is registered), so this is what context.User already holds when
    /// AuthenticationMiddleware runs.
    /// </summary>
    private static ClaimsPrincipal FrameworkPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, SubjectId.ToString()), new Claim("scope", "alerts.readwrite")],
            "Bearer"));

    private static (AuthenticationMiddleware Middleware, DefaultHttpContext Context) Build(bool isMember)
    {
        var memberService = new Mock<ITenantMemberService>();
        memberService
            .Setup(s => s.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isMember);

        // A real provider, not a mocked IServiceScopeFactory: the middleware resolves a
        // NocturneDbContext from the scope to read IsPlatformAdmin, and a mock returning a null
        // scope throws into the catch that calls SetUnauthenticated — which makes every case,
        // including the ones that should succeed, look rejected.
        var dbName = $"auth_principal_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddSingleton(memberService.Object);
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName));
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "victim", "Victim", true, false);

        var authContext = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = SubjectId,
            TenantId = TenantId,
            SubjectName = "someone",
            Roles = ["admin"],
        };

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [new StubHandler(authContext)],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        return (middleware, context);
    }

    [Fact]
    public async Task Non_member_of_the_resolved_tenant_gets_no_authenticated_principal()
    {
        var (middleware, context) = Build(isMember: false);

        await middleware.InvokeAsync(context);

        context.Items["AuthContext"].Should().BeOfType<AuthContext>()
            .Which.IsAuthenticated.Should().BeFalse();

        context.User.Identity?.IsAuthenticated.Should().NotBe(true,
            "[Authorize] reads the principal, so leaving one populated after a membership "
            + "rejection authorizes the caller against the tenant they were rejected from");
        context.User.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task Member_of_the_resolved_tenant_keeps_an_authenticated_principal()
    {
        var (middleware, context) = Build(isMember: true);

        await middleware.InvokeAsync(context);

        context.Items["AuthContext"].Should().BeOfType<AuthContext>()
            .Which.IsAuthenticated.Should().BeTrue();
        context.User.Identity?.IsAuthenticated.Should().BeTrue();
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(SubjectId.ToString());
    }

    [Fact]
    public async Task Rejected_credential_does_not_inherit_the_framework_principal()
    {
        // The other half of the same vulnerability. When the handler chain REJECTS a credential the
        // middleware never entered the IsAuthenticated branch, so it never assigned context.User —
        // and the principal the framework scheme built ahead of it survived untouched. The
        // membership check cannot catch this either: it only runs for IsAuthenticated: true. So a
        // token pinned to another tenant, a revoked grant, or a credential presented on a share host
        // still satisfied bare [Authorize] on ~40 V4 controllers, including the sensor-glucose read.
        var memberService = new Mock<ITenantMemberService>();
        var dbName = $"auth_principal_reject_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddSingleton(memberService.Object);
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName));
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "victim", "Victim", true, false);
        context.User = FrameworkPrincipal();

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [new RejectingHandler()],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        await middleware.InvokeAsync(context);

        context.Items["AuthContext"].Should().BeOfType<AuthContext>()
            .Which.IsAuthenticated.Should().BeFalse();

        context.User.Identity?.IsAuthenticated.Should().NotBe(true,
            "a credential the chain rejected must not keep the framework scheme's principal");
        context.User.Claims.Should().BeEmpty();
        context.GetGrantedScopes().Should().BeEmpty();
    }
}
