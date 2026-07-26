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
        auth!.ClientId.Should().Be(ScalarAuthProvider.ScalarClientId);
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
    public async Task PrepareAsync_AddsASecondRedirectUri_WithoutDuplicating()
    {
        var tenantId = SeedTenant("demo", isDemo: true, withDemoMember: true);
        var provider = BuildProvider();

        // A fresh provider per call so the in-memory client cache does not mask the
        // second registration.
        await provider.PrepareAsync(BuildContext("demo.nocturne.run"));
        await BuildProvider().PrepareAsync(BuildContext("demo.nocturne.run", proto: "http"));
        await BuildProvider().PrepareAsync(BuildContext("demo.nocturne.run"));

        await using var db = new NocturneDbContext(_dbOptions);
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().BeEquivalentTo([
                "https://demo.nocturne.run/scalar",
                "http://demo.nocturne.run/scalar",
            ]);
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
