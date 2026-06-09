using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

[Trait("Category", "Unit")]
public class CarrierResettingDbContextFactoryTests
{
    // A pooled context that last served a public share for another tenant: every carrier is
    // dirty, which is exactly what pooling leaves behind because it does not reset custom
    // properties between leases.
    private static NocturneDbContext StalePooledContext() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options)
        {
            TenantId = Guid.NewGuid(),
            AuditContext = Mock.Of<IAuditContext>(),
            IsShareContext = true,
            VisibleCategories = "glucose.read,treatments.read",
        };

    private static void ShouldBeFailClosed(NocturneDbContext context)
    {
        context.TenantId.Should().Be(Guid.Empty, "a fresh lease must not inherit a prior tenant");
        context.AuditContext.Should().BeNull("a fresh lease must not inherit a prior audit context");
        context.IsShareContext.Should().BeFalse("a fresh lease must not inherit a prior share marker");
        context.VisibleCategories.Should().BeNull("a fresh lease must not inherit a prior share's category CSV");
    }

    [Fact]
    public void CreateDbContext_ResetsEveryCarrier_OnAStalePooledContext()
    {
        var pooled = StalePooledContext();
        var inner = new Mock<IDbContextFactory<NocturneDbContext>>();
        inner.Setup(f => f.CreateDbContext()).Returns(pooled);

        var factory = new CarrierResettingDbContextFactory(inner.Object);
        using var leased = factory.CreateDbContext();

        leased.Should().BeSameAs(pooled, "the decorator mutates the leased context in place, it does not replace it");
        ShouldBeFailClosed(leased);
    }

    [Fact]
    public async Task CreateDbContextAsync_ResetsEveryCarrier_OnAStalePooledContext()
    {
        var pooled = StalePooledContext();
        var inner = new Mock<IDbContextFactory<NocturneDbContext>>();
        inner.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(pooled);

        var factory = new CarrierResettingDbContextFactory(inner.Object);
        await using var leased = await factory.CreateDbContextAsync();

        leased.Should().BeSameAs(pooled);
        ShouldBeFailClosed(leased);
    }
}
