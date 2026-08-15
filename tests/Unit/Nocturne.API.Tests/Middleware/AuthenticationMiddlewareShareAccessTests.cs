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
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
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

    private AuthenticationMiddleware Build(params IAuthHandler[] handlers) =>
        Build(Mock.Of<IServiceScopeFactory>(), handlers);

    private AuthenticationMiddleware Build(IServiceScopeFactory scopeFactory, params IAuthHandler[] handlers) => new(
        next: _ => Task.CompletedTask,
        logger: NullLogger<AuthenticationMiddleware>.Instance,
        handlers: handlers,
        environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
        publicAccessCacheService: _publicAccess,
        oidcOptions: Options.Create(new OidcOptions()),
        scopeFactory: scopeFactory);

    /// <summary>
    /// A real session-cookie credential for the seeded member subject, plus the scope factory the
    /// middleware needs to resolve it (subject lookup for the platform-admin flag).
    /// </summary>
    private (SessionCookieHandler Handler, IServiceScopeFactory ScopeFactory, string Token) RealSessionCredential()
    {
        var jwt = new JwtService(
            Options.Create(new JwtOptions { SecretKey = new string('k', 48) }),
            NullLogger<JwtService>.Instance);

        var token = jwt.GenerateAccessToken(
            new SubjectInfo { Id = TestDatabaseSeeder.TestSubjectId, Name = "owner" },
            permissions: ["*"],
            roles: ["Owner"]);

        // Sanity: the credential really is valid, so a "not authenticated" result below is the
        // share gate at work rather than a token that would have been rejected anyway.
        jwt.ValidateAccessToken(token).IsValid.Should().BeTrue();

        var services = new ServiceCollection();
        services.AddSingleton<IJwtService>(jwt);
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(_dbName));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var handler = new SessionCookieHandler(
            scopeFactory, NullLogger<SessionCookieHandler>.Instance, Options.Create(new OidcOptions()));

        return (handler, scopeFactory, token);
    }

    private static DefaultHttpContext ContextFor(bool shareAccess)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        // The seeded subject is a member of the seeded tenant; the middleware's membership check
        // is orthogonal to the share gate under test, so it is satisfied rather than exercised.
        services.AddSingleton(Mock.Of<ITenantMemberService>(m =>
            m.IsMemberAsync(TestDatabaseSeeder.TestSubjectId, TestDatabaseSeeder.TenantId) == Task.FromResult(true)));
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
    public async Task Share_access_never_grants_beyond_the_shareable_read_scopes()
    {
        // The Clinician role also carries therapy.read and alerts.read; a superuser or
        // readwrite grant on the Public membership is possible via the member-permissions
        // API. None of that may reach an anonymous visitor: the share host resolves to at
        // most the shareable read scopes.
        SetPublicDirectPermissions(["*"]);

        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var scopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
        scopes.Should().NotBeNull();
        scopes.Should().BeSubsetOf(TenantPermissions.PublicShareScopes,
            "a superuser grant on the Public membership must degrade to public read access");
        scopes.Should().Contain(OAuthScopes.GlucoseRead);
    }

    [Fact]
    public async Task Share_access_never_admits_a_tenant_administration_atom()
    {
        // The member-permissions API can write any atom onto the Public membership, and the
        // administration atoms are now part of the grantable scope vocabulary. The share host
        // must still resolve to at most the shareable read scopes.
        SetPublicDirectPermissions([
            OAuthScopes.GlucoseRead,
            TenantPermissions.MembersManage,
            TenantPermissions.RolesManage,
            TenantPermissions.TenantSettings,
            TenantPermissions.AuditRead,
            TenantPermissions.SharingManage,
        ]);

        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var scopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
        scopes.Should().NotBeNull();
        scopes.Should().BeSubsetOf(TenantPermissions.PublicShareScopes);
        scopes.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);

        var trie = ctx.Items["PermissionTrie"] as PermissionTrie;
        trie!.Check(TenantPermissions.MembersManage).Should().BeFalse();
        trie.Check(TenantPermissions.AuditRead).Should().BeFalse();
    }

    [Fact]
    public async Task Share_with_only_heartrate_and_stepcount_still_carries_a_nonempty_trie()
    {
        // heartrate.read/stepcount.read have no legacy api:* equivalent, so a trie derived
        // purely from ScopeTranslator.ToPermissions would be empty and the fallback
        // HasPermissions policy would 401 the whole share despite valid grants.
        SetPublicDirectPermissions([OAuthScopes.HeartRateRead, OAuthScopes.StepCountRead]);

        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var scopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
        scopes.Should().BeEquivalentTo([OAuthScopes.HeartRateRead, OAuthScopes.StepCountRead]);
        var trie = ctx.Items["PermissionTrie"] as PermissionTrie;
        trie!.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Share_access_accepts_legacy_trie_vocabulary_grants()
    {
        // Pre-scope-era Public memberships (and anything written through the unvalidated
        // member-permissions API) may carry legacy api:* strings; they resolved before the
        // scope-vocabulary fix and must keep resolving after it.
        SetPublicDirectPermissions(["api:entries:read"]);

        var ctx = ContextFor(shareAccess: true);

        await Build().InvokeAsync(ctx);

        var scopes = ctx.Items["GrantedScopes"] as IReadOnlySet<string>;
        scopes.Should().Contain(OAuthScopes.GlucoseRead);
        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().VisibleCategoriesCsv
            .Should().Contain("glucose.read");
    }

    private void SetPublicDirectPermissions(List<string> permissions)
    {
        using var db = TestDbContextFactory.CreateInMemoryContext(_dbName);
        var member = db.TenantMembers
            .Include(m => m.MemberRoles)
            .Single(m => m.SubjectId == TestDatabaseSeeder.PublicSubjectId);
        db.TenantMemberRoles.RemoveRange(member.MemberRoles);
        member.DirectPermissions = permissions;
        db.SaveChanges();
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

    [Fact]
    public async Task Share_host_ignores_a_real_session_cookie()
    {
        // Session cookies are scoped to ".{base-domain}" so one sign-in reaches the apex
        // dashboard and every tenant subdomain. That also means the browser now presents them on
        // {token}.share.{base-domain}, a host that must stay anonymous for everyone: an owner
        // following their own share link has to see exactly what a stranger sees. Unlike the
        // fake-handler test above, this drives the real SessionCookieHandler with a genuinely
        // valid JWT, so it fails if the share gate ever moves below credential resolution.
        var (handler, scopeFactory, token) = RealSessionCredential();

        var ctx = ContextFor(shareAccess: true);
        ctx.Request.Headers.Cookie = $".Nocturne.AccessToken={token}";

        await Build(scopeFactory, handler).InvokeAsync(ctx);

        var auth = ctx.Items["AuthContext"] as AuthContext;
        auth!.IsAuthenticated.Should().BeFalse(
            "a valid session cookie must not authenticate on a share host");
        auth.SubjectId.Should().Be(TestDatabaseSeeder.PublicSubjectId,
            "the share host resolves to the anonymous Public subject, never the cookie's owner");
        auth.Permissions.Should().NotContain("*");
        ctx.User.Identity?.IsAuthenticated.Should().NotBe(true,
            "[Authorize] reads the principal, so it must be anonymous too");
    }

    [Fact]
    public async Task Bare_host_honours_the_same_session_cookie()
    {
        // The sensitivity guard for the test above: the identical credential on a non-share host
        // authenticates. Without this, "not authenticated" could pass for the wrong reason (an
        // unparseable token, a misnamed cookie) and the share gate would go untested.
        var (handler, scopeFactory, token) = RealSessionCredential();

        var ctx = ContextFor(shareAccess: false);
        ctx.Request.Headers.Cookie = $".Nocturne.AccessToken={token}";

        await Build(scopeFactory, handler).InvokeAsync(ctx);

        var auth = ctx.Items["AuthContext"] as AuthContext;
        auth!.IsAuthenticated.Should().BeTrue();
        auth.SubjectId.Should().Be(TestDatabaseSeeder.TestSubjectId);
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
