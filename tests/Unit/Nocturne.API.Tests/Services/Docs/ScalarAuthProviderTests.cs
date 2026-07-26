using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Docs;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Docs;

/// <summary>
/// The Scalar reference is served on every host without tenant resolution or
/// authentication, so what it hands the browser is decided entirely here: an OAuth client
/// for the host's tenant, and a bearer token only when that tenant is a demo.
/// </summary>
public class ScalarAuthProviderTests : IDisposable
{
    private const string BaseDomain = "nocturne.run";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<ISessionService> _sessionService = new();

    public ScalarAuthProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions);
        seed.Database.EnsureCreated();

        _sessionService
            .Setup(s => s.IssueSessionAsync(
                It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("demo-access-token", "refresh", 3600));
    }

    [Fact]
    public async Task PrepareAsync_RegistersClientAndPrefillsToken_OnADemoHost()
    {
        var tenantId = SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = BuildContext("demo.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        var auth = Auth(context);
        auth.Should().NotBeNull();
        auth!.ClientId.Should().NotBeNullOrWhiteSpace();
        auth.RedirectUri.Should().Be("https://demo.nocturne.run/scalar");
        auth.BearerToken.Should().Be("demo-access-token");

        await using var db = new NocturneDbContext(_dbOptions);
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId && c.SoftwareId == ScalarAuthProvider.ScalarSoftwareId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().Equal("https://demo.nocturne.run/scalar");
    }

    [Fact]
    public async Task PrepareAsync_RegistersClientWithoutAToken_OnARealTenantHost()
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext("rhys.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        var auth = Auth(context);
        auth.Should().NotBeNull();
        auth!.RedirectUri.Should().Be("https://rhys.nocturne.run/scalar");
        auth.BearerToken.Should().BeNull("only a demo tenant's account may be handed out");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PrepareAsync_DoesNothing_OnAShareHost()
    {
        SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = BuildContext("sometoken.share.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull("a share link grants read-only anonymous access only");
        await using var db = new NocturneDbContext(_dbOptions);
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_DoesNothing_ForAnUnknownSlug()
    {
        SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = BuildContext("nope.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_DoesNothing_ForAnInactiveTenant()
    {
        SeedTenant("demo", isDemo: true, withDemoMember: true, isActive: false);
        var context = BuildContext("demo.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_MarksTheDemoResponseUncacheable()
    {
        SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = BuildContext("demo.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        context.Response.Headers.CacheControl.ToString()
            .Should().Contain("no-store", "the page carries a bearer token");
    }

    [Fact]
    public async Task PrepareAsync_LeavesARealTenantResponseCacheable()
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext("rhys.nocturne.run");

        await BuildProvider().PrepareAsync(context);

        context.Response.Headers.CacheControl.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_DoesNotDuplicateARedirectUri()
    {
        var tenantId = SeedTenant("demo", isDemo: true, withDemoMember: true);

        // A fresh provider per call so the in-memory client cache does not mask a
        // repeated registration.
        await BuildProvider().PrepareAsync(BuildContext("demo.nocturne.run"));
        await BuildProvider().PrepareAsync(BuildContext("demo.nocturne.run"));

        await using var db = new NocturneDbContext(_dbOptions);
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().Equal("https://demo.nocturne.run/scalar");
    }

    /// <summary>
    /// The forwarded headers are client-controllable. A value that resolves a real tenant
    /// while carrying a different effective authority must never be persisted as a
    /// redirect URI: authorize-time matching is byte-exact, so it would let an attacker
    /// have that tenant's authorization codes delivered to a host they control.
    /// </summary>
    [Theory]
    [InlineData("rhys.nocturne.run:8443@attacker.example")] // userinfo — the real host is attacker.example
    [InlineData("rhys.nocturne.run@attacker.example")]
    [InlineData("rhys.nocturne.run:443, attacker.example")] // header list — only the first was slug-matched
    [InlineData("rhys.nocturne.run/../..@attacker.example")]
    [InlineData("rhys.nocturne.run\\@attacker.example")]
    [InlineData("rhys.nocturne.run#@attacker.example")]
    [InlineData("rhys.nocturne.run?x=1")]
    [InlineData("rhys.nocturne.run:99999")]                 // out-of-range port
    [InlineData("rhys.nocturne.run:80:80")]
    public async Task PrepareAsync_RejectsAForwardedHostWhoseAuthorityIsNotTheResolvedTenant(string forwardedHost)
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext(forwardedHost);

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();

        await using var db = new NocturneDbContext(_dbOptions);
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync())
            .Should().BeFalse("no OAuth client may be registered from an unparseable host");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("file")]
    [InlineData("HTTPS evil")]
    public async Task PrepareAsync_RejectsANonHttpForwardedProto(string proto)
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext("rhys.nocturne.run", proto);

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_RejectsCleartextHttpOnAPublicHost()
    {
        // RedirectUriValidator treats http on a non-loopback host as invalid for
        // registration; authorization codes must not be issued over plaintext.
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext("rhys.nocturne.run", proto: "http");

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();
        await using var db = new NocturneDbContext(_dbOptions);
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_DoesNotBadgeTheClientAsKnown()
    {
        // The consent screen suppresses its "app not recognized" warning for known
        // clients. This row is created by an unauthenticated request.
        var tenantId = SeedTenant("rhys", isDemo: false, withDemoMember: false);

        await BuildProvider().PrepareAsync(BuildContext("rhys.nocturne.run"));

        await using var db = new NocturneDbContext(_dbOptions);
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        client.IsKnown.Should().BeFalse();
        client.ClientId.Should().NotBe(ScalarAuthProvider.ScalarSoftwareId);
    }

    [Fact]
    public async Task PrepareAsync_CapsTheRedirectUriList()
    {
        var tenantId = SeedTenant("rhys", isDemo: false, withDemoMember: false);

        // Distinct ports are all legitimate origins, so each is a new entry — the cap is
        // what stops an unauthenticated caller growing the row without bound.
        for (var port = 8001; port <= 8010; port++)
        {
            await BuildProvider().PrepareAsync(BuildContext($"rhys.nocturne.run:{port}"));
        }

        await using var db = new NocturneDbContext(_dbOptions);
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().HaveCount(5);
    }

    [Fact]
    public async Task PrepareAsync_ReusesOneTokenAcrossPageLoads()
    {
        SeedTenant("demo", isDemo: true, withDemoMember: true);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await BuildProvider(cache).PrepareAsync(BuildContext("demo.nocturne.run"));
        await BuildProvider(cache).PrepareAsync(BuildContext("demo.nocturne.run"));
        await BuildProvider(cache).PrepareAsync(BuildContext("demo.nocturne.run"));

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the docs page must not mint a session per view — /scalar bypasses rate limiting");
    }

    private static ScalarAuthContext? Auth(HttpContext context) =>
        context.Items[ScalarAuthContext.HttpContextItemKey] as ScalarAuthContext;

    private Guid SeedTenant(string slug, bool isDemo, bool withDemoMember, bool isActive = true)
    {
        using var db = new NocturneDbContext(_dbOptions);

        var tenant = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            DisplayName = slug,
            IsActive = isActive,
            IsDemo = isDemo,
        };
        db.Add(tenant);

        if (withDemoMember)
        {
            var subject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = DemoTenantService.DemoMemberName,
                IsActive = true,
            };
            db.Subjects.Add(subject);
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                SubjectId = subject.Id,
                Username = DemoTenantService.DemoMemberUsername,
            });
        }

        db.SaveChanges();
        return tenant.Id;
    }

    private static HttpContext BuildContext(string host, string proto = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/scalar";
        context.Request.Headers["X-Forwarded-Host"] = host;
        context.Request.Headers["X-Forwarded-Proto"] = proto;
        return context;
    }

    private ScalarAuthProvider BuildProvider(IMemoryCache? cache = null)
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        var demoTenantService = new DemoTenantService(
            dbFactory.Object,
            new Mock<ITenantService>().Object,
            TestPublicAccessCache.Create(),
            new Mock<ILogger<DemoTenantService>>().Object);

        return new ScalarAuthProvider(
            dbFactory.Object,
            demoTenantService,
            _sessionService.Object,
            new RedirectUriValidator(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
            new Mock<ILogger<ScalarAuthProvider>>().Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
