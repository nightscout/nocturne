using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for cache behavior in domain services
/// </summary>
public class ServiceCacheTests
{
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<ITenantService> _tenants = new();
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<ILogger<StatusService>> _mockStatusLogger;
    private readonly Mock<ITenantAccessor> _mockTenantAccessor;

    public ServiceCacheTests()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockStatusLogger = new Mock<ILogger<StatusService>>();
        _mockTenantAccessor = MockTenantAccessor.Create();

        _mockDemoModeService.Setup(x => x.IsEnabled).Returns(false);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Cache")]
    public async Task GetSystemStatusAsync_CacheHit_ReturnsCachedStatus()
    {
        // Arrange
        var cachedStatus = new StatusResponse
        {
            Status = "ok",
            Name = "Test Nocturne",
            Version = "1.0.0",
            ServerTime = DateTime.UtcNow,
        };

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedStatus);

        var configurationData = new Dictionary<string, string?>
        {
            ["Nightscout:SiteName"] = "Test Nocturne",
        };

        var mockConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationData)
            .Build();

        var dbContext = TestDbContextFactory.CreateInMemoryContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            Roles = new List<string> { "readable" },
            Permissions = new List<string> { "api:*:read" },
            Scopes = new List<string> { "api:*:read" },
        };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        var tenantDbContextFactory = new Mock<INocturneDbContextFactory>();
        tenantDbContextFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext());
        var appearanceReader = new Mock<ITenantAppearanceReader>();
        appearanceReader
            .Setup(r => r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences());
        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            new CacheFlights(),
            _mockDemoModeService.Object,
            tenantDbContextFactory.Object,
            new TenantRepository(tenantDbContextFactory.Object), new SubjectRepository(tenantDbContextFactory.Object), new RoleRepository(tenantDbContextFactory.Object), new OidcProviderRepository(tenantDbContextFactory.Object),
            httpContextAccessor,
            _mockTenantAccessor.Object,
            TestPublicAccessCache.Create(),
            new TenantGlucoseUnits(_mockTenantAccessor.Object, appearanceReader.Object, _tenants.Object),
            new GlucoseStatusClassifier(mockConfiguration, NullLogger<GlucoseStatusClassifier>.Instance),
            NoAlertRules(),
            Mock.Of<ICategoryReadContext>(),
            TimeProvider.System,
            _mockStatusLogger.Object
        );

        // Act
        var result = await statusService.GetSystemStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(cachedStatus.Name, result.Name);
        Assert.Equal(cachedStatus.Status, result.Status);

        // Verify cache was checked
        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Cache")]
    public async Task GetSystemStatusAsync_CacheMiss_GeneratesAndCachesStatus()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((StatusResponse?)null);

        var configurationData = new Dictionary<string, string?>
        {
            ["Nightscout:SiteName"] = "Test Site",
            ["Features:CareportalEnabled"] = "true",
            ["Display:TimeFormat"] = "12",
            ["Display:NightMode"] = "false",
            ["Display:EditMode"] = "true",
            ["Alarms:UrgentHigh:Enabled"] = "true",
            ["Alarms:High:Enabled"] = "true",
            ["Alarms:Low:Enabled"] = "true",
            ["Alarms:UrgentLow:Enabled"] = "true",
            ["Alarms:TimeAgoWarn:Enabled"] = "true",
            ["Alarms:TimeAgoUrgent:Enabled"] = "true",
            ["Thresholds:BgHigh"] = "260",
            ["Thresholds:BgTargetTop"] = "180",
            ["Thresholds:BgTargetBottom"] = "80",
            ["Thresholds:BgLow"] = "55",
            ["Display:Units"] = "mg/dl",
            ["Display:ShowRawBG"] = "never",
            ["Display:CustomTitle"] = "",
            ["Display:Theme"] = "default",
            ["Display:ShowPlugins"] = "",
            ["Display:ShowForecast"] = "",
            ["Localization:Language"] = "en",
            ["Display:ScaleY"] = "log",
            ["Features:Enable"] = "",
        };

        var mockConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationData)
            .Build();

        var dbContext = TestDbContextFactory.CreateInMemoryContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            Roles = new List<string> { "readable" },
            Permissions = new List<string> { "api:*:read" },
            Scopes = new List<string> { "api:*:read" },
        };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        var tenantDbContextFactory = new Mock<INocturneDbContextFactory>();
        tenantDbContextFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext());
        var appearanceReader = new Mock<ITenantAppearanceReader>();
        appearanceReader
            .Setup(r => r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences());
        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            new CacheFlights(),
            _mockDemoModeService.Object,
            tenantDbContextFactory.Object,
            new TenantRepository(tenantDbContextFactory.Object), new SubjectRepository(tenantDbContextFactory.Object), new RoleRepository(tenantDbContextFactory.Object), new OidcProviderRepository(tenantDbContextFactory.Object),
            httpContextAccessor,
            _mockTenantAccessor.Object,
            TestPublicAccessCache.Create(),
            new TenantGlucoseUnits(_mockTenantAccessor.Object, appearanceReader.Object, _tenants.Object),
            new GlucoseStatusClassifier(mockConfiguration, NullLogger<GlucoseStatusClassifier>.Instance),
            NoAlertRules(),
            Mock.Of<ICategoryReadContext>(),
            TimeProvider.System,
            _mockStatusLogger.Object
        );

        // Act
        var result = await statusService.GetSystemStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Site", result.Name);
        Assert.Equal("ok", result.Status);

        _mockCacheService.Verify(
            x =>
                x.SetAsync(
                    "status:system:00000000-0000-0000-0000-000000000001",
                    It.IsAny<StatusResponse>(),
                    TimeSpan.FromMinutes(2),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    private static IAlertRepository NoAlertRules()
    {
        var alerts = new Mock<IAlertRepository>();
        alerts.Setup(a => a.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return alerts.Object;
    }
}
