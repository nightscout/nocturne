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
using Nocturne.Infrastructure.Cache.Abstractions;
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

    [Fact]
    public async Task PrepareAsync_StopsAddingRedirectUrisAtTheCap()
    {
        // Unreachable by construction — the URI comes from configuration plus the stored slug,
        // and only https survives on a public host — but the row is written by an unauthenticated
        // request, so the bound must not rest on that argument staying true. Exercised directly
        // because nothing else can reach it.
        var tenantId = SeedTenant("demo", isDemo: true, withDemoMember: true);

        await BuildProvider().PrepareAsync(BuildContext("demo.nocturne.run"));

        await using (var seed = new NocturneDbContext(_dbOptions))
        {
            var client = await seed.OAuthClients.IgnoreQueryFilters()
                .SingleAsync(c => c.TenantId == tenantId);
            client.RedirectUris = JsonSerializer.Serialize(
                Enumerable.Range(0, ScalarAuthProvider.MaxRedirectUris)
                    .Select(i => $"https://origin{i}.example/scalar")
                    .ToList());
            await seed.SaveChangesAsync();
        }

        var context = BuildContext("demo.nocturne.run");
        await BuildProvider().PrepareAsync(context);

        context.Items.Should().NotContainKey(ScalarAuthContext.HttpContextItemKey,
            "at the cap the provider declines rather than growing the row");

        await using var db = new NocturneDbContext(_dbOptions);
        var after = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(after.RedirectUris)
            .Should().HaveCount(ScalarAuthProvider.MaxRedirectUris);
    }

    /// <summary>
    /// The request host is client-controllable — the gateway forwards X-Forwarded-Host
    /// untouched and UseForwardedHeaders trusts any proxy. It may therefore only select a
    /// tenant, never reach the redirect URI: byte-exact authorize matching means a
    /// persisted attacker origin would have that tenant's authorization codes delivered to
    /// a host they control.
    /// </summary>
    [Theory]
    [InlineData("attacker.example")]                        // belongs to nobody
    [InlineData("evil.attacker.example")]
    [InlineData("nocturne.run.attacker.example")]           // base domain as a prefix
    [InlineData("rhys.nocturne.run.attacker.example")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("0x7f.1")]                                  // parses as loopback
    [InlineData("[::1]")]
    public async Task PrepareAsync_RejectsAHostThisDeploymentDoesNotServe(string host)
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext(host);

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();

        await using var db = new NocturneDbContext(_dbOptions);
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync())
            .Should().BeFalse("a foreign host must not register a redirect URI on any tenant");
    }

    [Fact]
    public async Task PrepareAsync_RejectsAForeignHost_EvenOnASingleTenantInstall()
    {
        // The apex branch serves single-tenant installs. It must not treat a host that is
        // not the configured apex as the apex, or the sole tenant absorbs any origin.
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext("attacker.example");

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_ResolvesTheSoleTenant_OnTheConfiguredApex()
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext(BaseDomain);

        await BuildProvider().PrepareAsync(context);

        Auth(context)!.RedirectUri.Should().Be($"https://{BaseDomain}/scalar");
    }

    [Theory]
    [InlineData("rhys.nocturne.run:8443")]
    [InlineData("rhys.nocturne.run:80")]
    public async Task PrepareAsync_RejectsAPortTheDeploymentIsNotServedOn(string host)
    {
        SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = BuildContext(host);

        await BuildProvider().PrepareAsync(context);

        Auth(context).Should().BeNull("a caller must not register origins differing by port");
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
                // As provisioning creates it. The lookup requires the flag, not just the
                // membership, so seeding it false would model a state the provider refuses.
                IsDemoSubject = true,
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

    /// <summary>
    /// Builds a request as the pipeline presents it to the provider: UseForwardedHeaders
    /// has already applied X-Forwarded-Host/-Proto onto Request.Host and Request.Scheme,
    /// so the provider reads those rather than the headers.
    /// </summary>
    private static HttpContext BuildContext(string host, string proto = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/scalar";
        context.Request.Host = new HostString(host);
        context.Request.Scheme = proto;
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
            new Mock<ICacheService>().Object,
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
