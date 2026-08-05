using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Admin;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Admin;

/// <summary>
/// Provisioning flips <c>tenants.is_demo</c>, and that column is carried on the cached
/// <see cref="TenantContext"/> that <see cref="TenantResolutionMiddleware"/> holds for minutes.
/// </summary>
/// <remarks>
/// Demo-ness now gates two things that a visitor needs on their first request:
/// <c>GET /api/v4/demo/session</c> 404s on a non-demo tenant, and <c>/api/v4/status</c> reports
/// <c>isDemo</c>, which is what the login page keys its auto-sign-in redirect off. A demo tenant
/// has no passkey and no owner, so serving a stale non-demo context leaves a login page nobody
/// can complete until the entry expires.
/// </remarks>
public class DemoAdminProvisionCacheTests : IDisposable
{
    private const string DemoSlug = "demo";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public DemoAdminProvisionCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions);
        seed.Database.EnsureCreated();
    }

    [Fact]
    public async Task Provision_EvictsTheCachedTenantContext()
    {
        // Whatever resolved the host before provisioning cached a context built from a row whose
        // is_demo was still false — CreateWithoutOwnerAsync makes the tenant reachable, so this
        // is the ordinary sequence, not a contrived one.
        var staleContext = new TenantContext(
            Guid.Empty, DemoSlug, "Nocturne Demo", IsActive: true, IsDemo: false);
        _cache.Set(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), staleContext);
        _cache.Set(TenantResolutionMiddleware.SoleTenantCacheKey, staleContext);

        var controller = BuildController();

        await controller.Provision(_cache, CancellationToken.None);

        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), out _)
            .Should().BeFalse("the next request has to see is_demo = true");
        _cache.TryGetValue(TenantResolutionMiddleware.SoleTenantCacheKey, out _)
            .Should().BeFalse("a single-tenant install resolves the apex through the sole-tenant key");
    }

    [Fact]
    public async Task UpdateStatus_EvictsWhenItChangesIsActive()
    {
        // IsActive rides on the same cached context as IsDemo, so deactivating the demo without
        // evicting keeps serving it for the cache lifetime.
        var controller = BuildController();
        await controller.Provision(_cache, CancellationToken.None);

        var staleContext = new TenantContext(
            Guid.Empty, DemoSlug, "Nocturne Demo", IsActive: true, IsDemo: true);
        _cache.Set(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), staleContext);
        _cache.Set(TenantResolutionMiddleware.SoleTenantCacheKey, staleContext);

        await controller.UpdateStatus(
            new DemoStatusPatchDto(IsActive: false), _cache, CancellationToken.None);

        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), out _)
            .Should().BeFalse("a deactivated demo must stop being served at once");
        _cache.TryGetValue(TenantResolutionMiddleware.SoleTenantCacheKey, out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task UpdateStatus_LeavesTheCacheAloneWhenOnlyTheScheduleChanges()
    {
        // The reset schedule is not carried on the tenant context, so evicting for it would throw
        // away a resolution the next request has to redo for nothing.
        var controller = BuildController();
        await controller.Provision(_cache, CancellationToken.None);

        _cache.Set(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), "kept");

        await controller.UpdateStatus(
            new DemoStatusPatchDto(NextResetAt: DateTime.UtcNow.AddHours(1)),
            _cache, CancellationToken.None);

        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), out _)
            .Should().BeTrue();
    }

    [Fact]
    public void EvictTenant_RemovesBothKeysAndLeavesOtherTenantsAlone()
    {
        var other = new TenantContext(Guid.NewGuid(), "other", "Other", IsActive: true, IsDemo: false);
        _cache.Set(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), "demo-entry");
        _cache.Set(TenantResolutionMiddleware.SoleTenantCacheKey, "sole-entry");
        _cache.Set(TenantResolutionMiddleware.TenantCacheKey("other"), other);

        TenantResolutionMiddleware.EvictTenant(_cache, DemoSlug);

        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey(DemoSlug), out _).Should().BeFalse();
        _cache.TryGetValue(TenantResolutionMiddleware.SoleTenantCacheKey, out _).Should().BeFalse();
        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey("other"), out TenantContext? kept)
            .Should().BeTrue("eviction is per tenant, not a cache flush");
        kept.Should().Be(other);
    }

    private DemoAdminController BuildController()
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        var tenantService = new Mock<ITenantService>();
        tenantService
            .Setup(t => t.CreateWithoutOwnerAsync(DemoSlug, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                using var db = new NocturneDbContext(_dbOptions);
                var tenant = new TenantEntity
                {
                    Id = Guid.CreateVersion7(),
                    Slug = DemoSlug,
                    DisplayName = "Nocturne Demo",
                    IsActive = true,
                };
                db.Add(tenant);
                db.SaveChanges();
                return new TenantCreatedDto(
                    tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, DateTime.UtcNow);
            });

        var demoTenantService = new DemoTenantService(
            dbFactory.Object,
            tenantService.Object,
            TestPublicAccessCache.Create(),
            new Mock<ICacheService>().Object,
            new Mock<ILogger<DemoTenantService>>().Object);

        return new DemoAdminController(tenantService.Object, demoTenantService, dbFactory.Object);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
