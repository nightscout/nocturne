using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Services;

[Trait("Category", "Unit")]
public class TenantDbContextFactoryTests
{
    private static Mock<IDbContextFactory<NocturneDbContext>> NewPool()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new NocturneDbContext(options);
        var pool = new Mock<IDbContextFactory<NocturneDbContext>>();
        pool.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ctx);
        return pool;
    }

    private static Mock<ITenantAccessor> ResolvedAccessor(Guid tenantId)
    {
        var accessor = MockTenantAccessor.Create(tenantId);
        return accessor;
    }

    private static Mock<ICategoryReadContext> Category(bool isShare, string? csv, bool fullHistory = false)
    {
        var category = new Mock<ICategoryReadContext>();
        category.Setup(c => c.IsShare).Returns(isShare);
        category.Setup(c => c.VisibleCategoriesCsv).Returns(csv);
        category.Setup(c => c.FullHistory).Returns(fullHistory);
        return category;
    }

    [Fact]
    public async Task CreateAsync_SetsTenantId_WhenTenantResolved()
    {
        var tenantId = Guid.NewGuid();

        var factory = new TenantDbContextFactory(NewPool().Object, ResolvedAccessor(tenantId).Object, categoryReadContext: null);
        await using var result = await factory.CreateAsync();

        result.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task CreateAsync_LeavesDefaultTenantId_WhenTenantNotResolved()
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.IsResolved).Returns(false);

        var factory = new TenantDbContextFactory(NewPool().Object, accessor.Object, categoryReadContext: null);
        await using var result = await factory.CreateAsync();

        result.TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task CreateAsync_CarriesShareMarkerAndCsv_WhenShare()
    {
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object, Category(isShare: true, csv: "glucose.read").Object);
        await using var result = await factory.CreateAsync();

        result.IsShareContext.Should().BeTrue();
        result.VisibleCategories.Should().Be("glucose.read");
        result.ShareFullHistory.Should().BeFalse("a share whose window was never resolved stays clamped to 24 hours");
    }

    [Fact]
    public async Task CreateAsync_CarriesFullHistory_WhenShareAllowsIt()
    {
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object,
            Category(isShare: true, csv: "glucose.read", fullHistory: true).Object);
        await using var result = await factory.CreateAsync();

        result.ShareFullHistory.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_NonShare_DoesNotCarryFullHistory()
    {
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object,
            Category(isShare: false, csv: null, fullHistory: true).Object);
        await using var result = await factory.CreateAsync();

        result.ShareFullHistory.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_ShareWithoutCsv_StaysShareWithNullCsv_FailClosed()
    {
        // A share whose CSV was never resolved (e.g. an auth gap) keeps IsShareContext=true
        // with a null CSV, so the RLS policy denies all categorized data rather than opening up.
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object, Category(isShare: true, csv: null).Object);
        await using var result = await factory.CreateAsync();

        result.IsShareContext.Should().BeTrue();
        result.VisibleCategories.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NonShare_LeavesShareContextOff()
    {
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object, Category(isShare: false, csv: null).Object);
        await using var result = await factory.CreateAsync();

        result.IsShareContext.Should().BeFalse();
        result.VisibleCategories.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NullCategoryContext_LeavesShareContextOff()
    {
        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object, categoryReadContext: null);
        await using var result = await factory.CreateAsync();

        result.IsShareContext.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_StampsScopedAuditContext()
    {
        // The MutationAuditInterceptor resolves the audit context via IHttpContextAccessor,
        // which is null in background services (connector syncs), where it falls back to
        // NocturneDbContext.AuditContext. Without this stamping, every V4 repository write
        // from a background scope was audited as a null-attributed user mutation (~1.5M
        // mutation_audit_log rows/day in production) instead of being skipped as system.
        var auditContext = Mock.Of<IAuditContext>(a => a.IsSystem == true);

        var factory = new TenantDbContextFactory(
            NewPool().Object, ResolvedAccessor(Guid.NewGuid()).Object, categoryReadContext: null,
            auditContext: auditContext);
        await using var result = await factory.CreateAsync();

        result.AuditContext.Should().BeSameAs(auditContext);
    }

    [Fact]
    public async Task CreateAsync_NoAuditContext_ClearsStaleAuditContextFromPooledContext()
    {
        // Pooling does not reset custom properties: assign unconditionally so a context that
        // last served an attributed scope cannot leak that attribution into the next lease.
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var pooled = new NocturneDbContext(options)
        {
            AuditContext = Mock.Of<IAuditContext>(),
        };
        var pool = new Mock<IDbContextFactory<NocturneDbContext>>();
        pool.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(pooled);

        var factory = new TenantDbContextFactory(
            pool.Object, ResolvedAccessor(Guid.NewGuid()).Object, categoryReadContext: null);
        await using var result = await factory.CreateAsync();

        result.AuditContext.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NonShare_ClearsStaleShareCarrierFromPooledContext()
    {
        // Pooling does not reset custom properties: a context that last served a share arrives
        // with IsShareContext=true and a CSV. A non-share lease must clear both, or the owner
        // (is_share would be true) gets wrongly locked out of their own data.
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var pooled = new NocturneDbContext(options)
        {
            IsShareContext = true,
            VisibleCategories = "glucose.read,treatments.read",
            ShareFullHistory = true,
        };
        var pool = new Mock<IDbContextFactory<NocturneDbContext>>();
        pool.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(pooled);

        var factory = new TenantDbContextFactory(
            pool.Object, ResolvedAccessor(Guid.NewGuid()).Object, Category(isShare: false, csv: null).Object);
        await using var result = await factory.CreateAsync();

        result.IsShareContext.Should().BeFalse("a non-share lease must clear a prior share's marker");
        result.VisibleCategories.Should().BeNull("a non-share lease must clear a prior share's CSV");
        result.ShareFullHistory.Should().BeFalse("a non-share lease must clear a prior share's history window");
    }
}
