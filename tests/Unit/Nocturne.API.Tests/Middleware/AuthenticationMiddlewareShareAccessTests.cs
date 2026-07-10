using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Verifies the public-read gate in <see cref="AuthenticationMiddleware"/>: public access is
/// granted only when <c>ShareAccess</c> is set (the {token}.share host), the bare host grants
/// nothing to anonymous callers, and the share host ignores credentials entirely (session-blind).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuthenticationMiddlewareShareAccessTests
{
    private readonly PublicAccessCacheService _publicAccess;
    private readonly string _dbName;

    public AuthenticationMiddlewareShareAccessTests()
    {
        _dbName = $"share_gate_{Guid.NewGuid()}";
        using (var seed = TestDbContextFactory.CreateInMemoryContext(_dbName))
        {
            TestDatabaseSeeder.Seed(seed);
        }

        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext(_dbName));

        _publicAccess = new PublicAccessCacheService(
            new MemoryCache(new MemoryCacheOptions()), factory.Object, NullLogger<PublicAccessCacheService>.Instance);
    }

    private AuthenticationMiddleware Build(params IAuthHandler[] handlers) => new(
        next: _ => Task.CompletedTask,
        logger: NullLogger<AuthenticationMiddleware>.Instance,
        handlers: handlers,
        environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
        publicAccessCacheService: _publicAccess,
        oidcOptions: Options.Create(new OidcOptions()),
        scopeFactory: Mock.Of<IServiceScopeFactory>());

    private static DefaultHttpContext ContextFor(bool shareAccess)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Items["TenantContext"] =
            new TenantContext(TestDatabaseSeeder.TenantId, "acme", "Acme", true, false);
        if (shareAccess)
        {
            ctx.Items["ShareAccess"] = true;
            // TenantResolutionMiddleware marks the share upstream; simulate that here so the
            // post-auth CSV set-point is exercised.
            ctx.RequestServices.GetRequiredService<ICategoryReadContext>().MarkShare();
        }
        return ctx;
    }

    [Fact]
    public async Task Share_access_grants_public_read_to_the_public_subject()
    {
        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var auth = ctx.Items["AuthContext"] as AuthContext;
        auth!.IsAuthenticated.Should().BeFalse();
        auth.SubjectId.Should().Be(TestDatabaseSeeder.PublicSubjectId);
        // The post-auth CSV set-point ran: the share carries a (possibly empty) visible-categories
        // value, never null — null on a share would fail-open at the policy.
        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().VisibleCategoriesCsv.Should().NotBeNull();
    }

    [Fact]
    public async Task Share_access_resolves_scope_vocabulary_grants_into_scopes_and_categories()
    {
        // Regression: the Public subject's grants are stored in the OAuth scope vocabulary
        // (glucose.read, ...), the same vocabulary member grants use. Translating them with
        // ScopeTranslator.FromPermissions (which understands only legacy api:* trie strings)
        // silently dropped every grant — GrantedScopes and the visible-categories CSV came out
        // empty, and the share RLS policy denied every row: the whole share dashboard rendered
        // empty while every request returned 200.
        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var scopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
        scopes.Should().NotBeNull();
        scopes!.Should().Contain(OAuthScopes.GlucoseRead,
            "the seeded Public membership grants glucose.read via the Clinician role");

        var csv = ctx.RequestServices.GetRequiredService<ICategoryReadContext>().VisibleCategoriesCsv;
        csv.Should().Contain("glucose.read").And.Contain("treatments.read");
    }

    [Fact]
    public async Task Share_access_stays_clamped_to_24_hours_when_limited()
    {
        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().FullHistory.Should().BeFalse(
            "the seeded Public membership has LimitTo24Hours=true");
    }

    [Fact]
    public async Task Share_access_carries_full_history_when_not_limited()
    {
        using (var db = TestDbContextFactory.CreateInMemoryContext(_dbName))
        {
            var member = db.TenantMembers.Single(m => m.SubjectId == TestDatabaseSeeder.PublicSubjectId);
            member.LimitTo24Hours = false;
            db.SaveChanges();
        }

        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().FullHistory.Should().BeTrue();
    }

    [Fact]
    public async Task Bare_host_grants_no_public_read_to_anonymous_callers()
    {
        var ctx = ContextFor(shareAccess: false);

        await Build().InvokeAsync(ctx);

        var auth = ctx.Items["AuthContext"] as AuthContext;
        auth!.IsAuthenticated.Should().BeFalse();
        auth.SubjectId.Should().BeNull("the bare host must not grant the Public subject's access");
    }

    [Fact]
    public async Task Share_host_ignores_a_valid_session_credential()
    {
        var ctx = ContextFor(shareAccess: true);

        await Build(new AlwaysAuthHandler()).InvokeAsync(ctx);

        var auth = ctx.Items["AuthContext"] as AuthContext;
        auth!.IsAuthenticated.Should().BeFalse("the share host must never honor credentials");
        auth.SubjectId.Should().Be(TestDatabaseSeeder.PublicSubjectId);
    }

    private sealed class AlwaysAuthHandler : IAuthHandler
    {
        public int Priority => 50;
        public string Name => "AlwaysAuth";

        public Task<AuthResult> AuthenticateAsync(HttpContext context) =>
            Task.FromResult(AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = Guid.NewGuid(),
                SubjectName = "real-user",
                Permissions = ["*"],
            }));
    }
}
