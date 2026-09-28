using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class DeduplicationReconciliationBackgroundServiceTests
{
    private static SqliteTestDatabase TwoActiveTenantsAndOneInactive() =>
        TestDbContextFactory.CreateSqlite()
            .SeedTenant(Guid.NewGuid(), "active-one")
            .SeedTenant(Guid.NewGuid(), "active-two")
            .SeedTenant(Guid.NewGuid(), "inactive-one", isActive: false);

    private static IServiceProvider BuildServiceProvider(
        SqliteTestDatabase db,
        Mock<IDeduplicationService> dedupMock)
    {
        var services = new ServiceCollection();

        db.AddToServices(services);
        services.AddActiveTenantSnapshot();

        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            mock.Setup(t => t.SetTenant(It.IsAny<TenantContext>()));
            return mock.Object;
        });

        services.AddScoped(_ => dedupMock.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReconcileAllTenantsAsync_CallsReconcileOncePerActiveTenant()
    {
        // Arrange
        using var db = TwoActiveTenantsAndOneInactive();

        var dedupMock = new Mock<IDeduplicationService>();
        dedupMock
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReconcileResult(0, true));

        var serviceProvider = BuildServiceProvider(db, dedupMock);

        var sut = new DeduplicationReconciliationBackgroundService(
            serviceProvider,
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<DeduplicationReconciliationBackgroundService>.Instance);

        // Act
        await sut.ReconcileAllTenantsAsync(CancellationToken.None);

        // Assert — once per ACTIVE tenant (2), not the inactive one
        dedupMock.Verify(
            d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ReconcileAllTenantsAsync_OneTenantThrows_OtherTenantsStillReconciled()
    {
        // Arrange
        using var db = TwoActiveTenantsAndOneInactive();

        var callCount = 0;
        var dedupMock = new Mock<IDeduplicationService>();
        dedupMock
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                // First active tenant throws; the loop must continue to the second.
                if (callCount == 1)
                    throw new InvalidOperationException("boom");
                return Task.FromResult(new ReconcileResult(0, true));
            });

        var serviceProvider = BuildServiceProvider(db, dedupMock);

        var sut = new DeduplicationReconciliationBackgroundService(
            serviceProvider,
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<DeduplicationReconciliationBackgroundService>.Instance);

        // Act — must not throw despite the first tenant failing
        await sut.ReconcileAllTenantsAsync(CancellationToken.None);

        // Assert — both active tenants were attempted
        dedupMock.Verify(
            d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
