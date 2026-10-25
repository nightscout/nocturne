using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

public class DatabaseCapacityControllerTests
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void IsGatedOnThePlatformAdminRole_LikeTheTenantController()
    {
        var gate = typeof(DatabaseCapacityController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        gate.Roles.Should().Be("platform_admin");
    }

    [Fact]
    public async Task Get_ReportsTheWholeDatabaseAndEachTenantsEstimate()
    {
        var big = new TenantDto(Guid.NewGuid(), "big", "Big", true, DateTime.UtcNow);
        var small = new TenantDto(Guid.NewGuid(), "small", "Small", true, DateTime.UtcNow);
        var tenants = new Mock<ITenantService>();
        tenants.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([big, small]);
        var storeSize = new Mock<IStoreSizeReader>();
        storeSize
            .Setup(x => x.ReadDatabaseSizeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSize(1000 * Mib, 300 * Mib));
        storeSize
            .Setup(x => x.EstimateAsync(big.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbStats { DataSize = 600 * Mib, IndexSize = 200 * Mib });
        storeSize
            .Setup(x => x.EstimateAsync(small.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbStats { DataSize = 80 * Mib, IndexSize = 20 * Mib });
        var controller = new DatabaseCapacityController(
            tenants.Object, storeSize.Object, Options.Create(new DbSizeOptions { MaxMiB = 2000 }), new MemoryCache(new MemoryCacheOptions()));

        var result = await controller.Get(CancellationToken.None);

        var capacity = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DatabaseCapacityDto>().Subject;
        capacity.DatabaseSizeBytes.Should().Be(1000 * Mib);
        capacity.IndexSizeBytes.Should().Be(300 * Mib);
        capacity.DataSizeBytes.Should().Be(700 * Mib);
        capacity.TenantCount.Should().Be(2);
        capacity.LargestTenantBytes.Should().Be(800 * Mib);
        capacity.AverageTenantBytes.Should().Be(450 * Mib);
        capacity.BudgetBytes.Should().Be(2000 * Mib);
        capacity.UsedPercent.Should().Be(50);
        capacity.Tenants.Should().BeEquivalentTo(new[]
        {
            new TenantStoreSizeDto(big.Id, "big", "Big", 600 * Mib, 200 * Mib),
            new TenantStoreSizeDto(small.Id, "small", "Small", 80 * Mib, 20 * Mib),
        });
    }

    [Fact]
    public async Task Get_ReportsZeroTenantFiguresForAnInstanceWithNoTenants()
    {
        var tenants = new Mock<ITenantService>();
        tenants.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var storeSize = new Mock<IStoreSizeReader>();
        storeSize
            .Setup(x => x.ReadDatabaseSizeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSize(10 * Mib, 2 * Mib));
        var controller = new DatabaseCapacityController(
            tenants.Object, storeSize.Object, Options.Create(new DbSizeOptions()), new MemoryCache(new MemoryCacheOptions()));

        var capacity = (DatabaseCapacityDto)((OkObjectResult)await controller.Get(CancellationToken.None)).Value!;

        capacity.TenantCount.Should().Be(0);
        capacity.AverageTenantBytes.Should().Be(0);
        capacity.LargestTenantBytes.Should().Be(0);
    }

    [Fact]
    public async Task Get_ComputesOnceForCallsWithinTheCacheWindow()
    {
        var tenants = new Mock<ITenantService>();
        tenants.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var storeSize = new Mock<IStoreSizeReader>();
        storeSize
            .Setup(x => x.ReadDatabaseSizeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSize(10 * Mib, 2 * Mib));
        var controller = new DatabaseCapacityController(
            tenants.Object, storeSize.Object, Options.Create(new DbSizeOptions()), new MemoryCache(new MemoryCacheOptions()));

        await controller.Get(CancellationToken.None);
        await controller.Get(CancellationToken.None);

        storeSize.Verify(x => x.ReadDatabaseSizeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
