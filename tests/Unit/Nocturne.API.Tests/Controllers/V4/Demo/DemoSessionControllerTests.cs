using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Demo;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Demo;

/// <summary>
/// Gating tests for the demo sign-in endpoint. The endpoint hands an anonymous
/// visitor a real session, so every guard that keeps it off non-demo tenants and
/// off the public share host is load-bearing.
/// </summary>
public class DemoSessionControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<ISessionService> _sessionService = new();

    public DemoSessionControllerTests()
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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 3600));
    }

    [Fact]
    public async Task CreateSession_IssuesSessionAndRedirects_ForDemoTenantWithDemoMember()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: true);

        var result = await controller.CreateSession(redirect: "/reports", format: null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/reports");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateSession_ReturnsNotFound_ForNonDemoTenant()
    {
        var tenantId = SeedTenant(isDemo: false, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: false);

        var result = await controller.CreateSession(redirect: null, format: null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        VerifyNoSessionIssued();
    }

    [Fact]
    public async Task CreateSession_ReturnsNotFound_OnShareHost()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: true, shareAccess: true);

        var result = await controller.CreateSession(redirect: null, format: null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        VerifyNoSessionIssued();
    }

    [Fact]
    public async Task CreateSession_ReturnsNotFound_WhenDemoTenantHasNoDemoMember()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: false);
        var controller = BuildController(tenantId, isDemo: true);

        var result = await controller.CreateSession(redirect: null, format: null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        VerifyNoSessionIssued();
    }

    [Fact]
    public async Task CreateSession_IgnoresOffSiteRedirect()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: true);

        var result = await controller.CreateSession(
            redirect: "https://evil.example/steal", format: null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/");
    }

    [Fact]
    public async Task CreateSession_RecordsNoVisitorIpOrUserAgent()
    {
        // Every visitor shares this subject, and /api/v4/account/sessions is readable by any
        // member of it — so recording the caller's address would show each visitor where
        // everyone else currently using the demo is connecting from. Asserted rather than left
        // to the absence of an argument, because putting IpAddress back would break nothing.
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: true);

        await controller.CreateSession(redirect: null, format: null, CancellationToken.None);

        _sessionService.Verify(
            s => s.IssueSessionAsync(
                It.IsAny<Guid>(),
                It.Is<SessionContext>(c => c.IpAddress == null && c.UserAgent == null),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a shared account must not accumulate the addresses of the people using it");
    }

    /// <summary>
    /// Holding the demo membership must not be enough on its own — the subject has to carry the flag
    /// too. Nothing downstream of the lookup re-examines whose subject it resolved, so without the
    /// check a real account under that username would be handed a session to whoever asked.
    /// </summary>
    [Fact]
    public async Task CreateSession_ReturnsNotFound_WhenTheDemoMemberIsNotADemoSubject()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);

        // Mutate only the flag: the membership still matches on username and is still unrevoked,
        // so nothing but the flag can account for the 404.
        await using (var db = new NocturneDbContext(_dbOptions))
        {
            var subject = await db.Subjects.SingleAsync(s => s.IsDemoSubject);
            subject.IsDemoSubject = false;
            await db.SaveChangesAsync();
        }

        var controller = BuildController(tenantId, isDemo: true);

        var result = await controller.CreateSession(redirect: null, format: null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        VerifyNoSessionIssued();
    }

    [Fact]
    public async Task CreateSession_ReturnsTokenPair_WhenFormatIsJson()
    {
        var tenantId = SeedTenant(isDemo: true, withDemoMember: true);
        var controller = BuildController(tenantId, isDemo: true);

        var result = await controller.CreateSession(redirect: null, format: "json", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<DemoSessionResponse>()
            .Which.AccessToken.Should().Be("access");
    }

    private Guid SeedTenant(bool isDemo, bool withDemoMember)
    {
        using var db = new NocturneDbContext(_dbOptions);

        var tenant = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = isDemo ? "demo" : "real",
            DisplayName = isDemo ? "Nocturne Demo" : "Real Tenant",
            IsActive = true,
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
                // membership, so seeding it false would model a state the endpoint refuses.
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

    private IDbContextFactory<NocturneDbContext> BuildDbFactory()
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));
        return dbFactory.Object;
    }

    private DemoSessionController BuildController(Guid tenantId, bool isDemo, bool shareAccess = false)
    {
        var demoTenantService = new DemoTenantService(
            BuildDbFactory(),
            new Mock<ITenantService>().Object,
            TestPublicAccessCache.Create(),
            new Mock<ICacheService>().Object,
            new Mock<ILogger<DemoTenantService>>().Object);

        var controller = new DemoSessionController(
            demoTenantService,
            MockTenantAccessor.Create(tenantId: tenantId, isDemo: isDemo).Object,
            _sessionService.Object,
            Options.Create(new OidcOptions
            {
                Cookie = new CookieSettings
                {
                    AccessTokenName = ".Nocturne.AccessToken",
                    RefreshTokenName = ".Nocturne.RefreshToken",
                    Secure = true,
                },
            }),
            new Mock<ILogger<DemoSessionController>>().Object);

        var httpContext = new DefaultHttpContext();
        if (shareAccess)
            httpContext.Items["ShareAccess"] = true;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Stand in for the framework's local-URL check so the redirect-target
        // assertions exercise the controller's own guard.
        var urlHelper = new Mock<IUrlHelper>();
        urlHelper.Setup(u => u.IsLocalUrl(It.IsAny<string>()))
            .Returns((string? url) => url is not null && url.StartsWith('/') && !url.StartsWith("//"));
        controller.Url = urlHelper.Object;

        return controller;
    }

    private void VerifyNoSessionIssued() => _sessionService.Verify(
        s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
        Times.Never);

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
