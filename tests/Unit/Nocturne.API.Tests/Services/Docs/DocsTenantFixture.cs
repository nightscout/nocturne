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

namespace Nocturne.API.Tests.Services.Docs;

/// <summary>
/// A database with tenants in it and a <see cref="ScalarAuthProvider"/> over it, as the
/// documentation paths see them: no tenant resolution, no authentication, nothing but the
/// request host to go on.
/// </summary>
internal sealed class DocsTenantFixture : IDisposable
{
    public const string BaseDomain = "nocturne.run";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    public DocsTenantFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions);
        seed.Database.EnsureCreated();

        SessionService
            .Setup(s => s.IssueSessionAsync(
                It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("demo-access-token", "refresh", 3600));
    }

    public Mock<ISessionService> SessionService { get; } = new();

    public NocturneDbContext Db() => new(_dbOptions);

    /// <summary>
    /// <paramref name="allowPublicDocs"/> defaults to on so a test that is not about the opt-in
    /// reaches the behaviour it is testing; the column itself defaults to off.
    /// </summary>
    public Guid SeedTenant(
        string slug,
        bool isDemo,
        bool withDemoMember,
        bool isActive = true,
        bool allowPublicDocs = true)
    {
        using var db = Db();

        var tenant = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            DisplayName = slug,
            IsActive = isActive,
            IsDemo = isDemo,
            AllowPublicDocs = allowPublicDocs,
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

    /// <summary>Flips the opt-in in the database only, leaving every cache untouched.</summary>
    public void SetAllowPublicDocs(Guid tenantId, bool allow)
    {
        using var db = Db();
        var tenant = db.Tenants.Single(t => t.Id == tenantId);
        tenant.AllowPublicDocs = allow;
        db.SaveChanges();
    }

    /// <summary>
    /// Builds a request as the pipeline presents it to the provider: UseForwardedHeaders
    /// has already applied X-Forwarded-Host/-Proto onto Request.Host and Request.Scheme,
    /// so the provider reads those rather than the headers.
    /// </summary>
    public static HttpContext BuildContext(string host, string proto = "https", string path = "/scalar")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Request.Scheme = proto;
        return context;
    }

    public ScalarAuthProvider BuildProvider(IMemoryCache? cache = null)
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Db);

        var demoTenantService = new DemoTenantService(
            dbFactory.Object,
            new Mock<ITenantService>().Object,
            TestPublicAccessCache.Create(),
            new Mock<ICacheService>().Object,
            new Mock<ILogger<DemoTenantService>>().Object);

        return new ScalarAuthProvider(
            dbFactory.Object,
            demoTenantService,
            SessionService.Object,
            new RedirectUriValidator(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
            new Mock<ILogger<ScalarAuthProvider>>().Object);
    }

    public void Dispose() => _connection.Dispose();
}
