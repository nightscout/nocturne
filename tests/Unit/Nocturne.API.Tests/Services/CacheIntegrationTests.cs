using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Core.Contracts.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Integration tests for cache behavior in domain services
/// </summary>
public class CacheIntegrationTests
{
    private readonly Mock<IEntryStore> _mockEntryStore;
    private readonly Mock<IEntryDecomposer> _mockEntryDecomposer;
    private readonly Mock<IEntryCache> _mockEntryCache;
    private readonly Mock<IDataEventSink<Entry>> _mockEntryEvents;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<ILogger<EntryService>> _mockEntryLogger;
    private readonly Mock<ILogger<StatusService>> _mockStatusLogger;
    private readonly Mock<ITenantAccessor> _mockTenantAccessor;

    public CacheIntegrationTests()
    {
        _mockEntryStore = new Mock<IEntryStore>();
        _mockEntryDecomposer = new Mock<IEntryDecomposer>();
        _mockEntryCache = new Mock<IEntryCache>();
        _mockEntryEvents = new Mock<IDataEventSink<Entry>>();
        _mockCacheService = new Mock<ICacheService>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockEntryLogger = new Mock<ILogger<EntryService>>();
        _mockStatusLogger = new Mock<ILogger<StatusService>>();
        _mockTenantAccessor = MockTenantAccessor.Create();

        _mockDemoModeService.Setup(x => x.IsEnabled).Returns(false);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    public async Task GetCurrentEntryAsync_CacheHit_ReturnsCachedEntry()
    {
        // Arrange
        var cachedEntry = new Entry
        {
            Id = "cached-1",
            Type = "sgv",
            Sgv = 150,
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _mockEntryCache
            .Setup(x => x.GetOrComputeCurrentAsync(
                It.IsAny<Func<Task<Entry?>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedEntry);

        var entryService = new EntryService(
            _mockEntryStore.Object,
            _mockEntryDecomposer.Object,
            _mockEntryCache.Object,
            _mockEntryEvents.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(cachedEntry.Id, result.Id);
        Assert.Equal(cachedEntry.Sgv, result.Sgv);

        // Verify cache was used
        _mockEntryCache.Verify(
            x => x.GetOrComputeCurrentAsync(
                It.IsAny<Func<Task<Entry?>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once
        );
        // Store should not have been called directly (cache handled it)
        _mockEntryStore.Verify(
            x => x.GetCurrentAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    public async Task GetCurrentEntryAsync_CacheMiss_FetchesFromStoreViaCacheCompute()
    {
        // Arrange
        var dbEntry = new Entry
        {
            Id = "db-1",
            Type = "sgv",
            Sgv = 120,
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Cache invokes the compute function on miss
        _mockEntryCache
            .Setup(x => x.GetOrComputeCurrentAsync(
                It.IsAny<Func<Task<Entry?>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<Task<Entry?>>, CancellationToken>(
                async (compute, ct) => await compute());

        _mockEntryStore
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dbEntry);

        var entryService = new EntryService(
            _mockEntryStore.Object,
            _mockEntryDecomposer.Object,
            _mockEntryCache.Object,
            _mockEntryEvents.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(dbEntry.Id, result.Id);
        Assert.Equal(dbEntry.Sgv, result.Sgv);

        // Verify store was called via the compute function
        _mockEntryStore.Verify(
            x => x.GetCurrentAsync(It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    public async Task CreateEntriesAsync_DecomposesToV4AndFiresEvents()
    {
        // Arrange
        var newEntries = new List<Entry>
        {
            new Entry
            {
                Id = "new-1",
                Type = "sgv",
                Sgv = 140,
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(It.IsAny<Entry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Core.Models.V4.DecompositionResult());

        var entryService = new EntryService(
            _mockEntryStore.Object,
            _mockEntryDecomposer.Object,
            _mockEntryCache.Object,
            _mockEntryEvents.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.CreateEntriesAsync(newEntries, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);

        // Verify decomposition happened and events fired (cache invalidation is handled by the event sink)
        _mockEntryDecomposer.Verify(
            x => x.DecomposeAsync(It.IsAny<Entry>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockEntryEvents.Verify(
            x => x.OnCreatedAsync(
                It.IsAny<IReadOnlyList<Entry>>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
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

        var mockDbContextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            _mockDemoModeService.Object,
            dbContext,
            mockDbContextFactory.Object,
            httpContextAccessor,
            _mockTenantAccessor.Object,
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
    [Trait("Category", "Integration")]
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

        var mockDbContextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            _mockDemoModeService.Object,
            dbContext,
            mockDbContextFactory.Object,
            httpContextAccessor,
            _mockTenantAccessor.Object,
            _mockStatusLogger.Object
        );

        // Act
        var result = await statusService.GetSystemStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Site", result.Name);
        Assert.Equal("ok", result.Status);

        // Verify cache was checked and result was cached
        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", It.IsAny<CancellationToken>()),
            Times.Once
        );
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
}
