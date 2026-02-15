using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Integration tests for cache behavior in domain services
/// </summary>
public class CacheIntegrationTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IOptions<CacheConfiguration>> _mockCacheConfig;
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<IEntryDecomposer> _mockEntryDecomposer;
    private readonly Mock<ILogger<EntryService>> _mockEntryLogger;
    private readonly Mock<ILogger<StatusService>> _mockStatusLogger;

    public CacheIntegrationTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockCacheService = new Mock<ICacheService>();
        _mockCacheConfig = new Mock<IOptions<CacheConfiguration>>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockEntryDecomposer = new Mock<IEntryDecomposer>();
        _mockEntryLogger = new Mock<ILogger<EntryService>>();
        _mockStatusLogger = new Mock<ILogger<StatusService>>();

        _mockCacheConfig.Setup(x => x.Value).Returns(new CacheConfiguration());
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

        _mockCacheService
            .Setup(x => x.GetAsync<Entry>("entries:current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedEntry);

        var entryService = new EntryService(
            _mockPostgreSqlService.Object,
            _mockSignalRBroadcastService.Object,
            _mockCacheService.Object,
            _mockCacheConfig.Object,
            _mockDemoModeService.Object,
            _mockEntryDecomposer.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(cachedEntry.Id, result.Id);
        Assert.Equal(cachedEntry.Sgv, result.Sgv);

        // Verify cache was checked but database was not called
        _mockCacheService.Verify(
            x => x.GetAsync<Entry>("entries:current", It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockPostgreSqlService.Verify(
            x => x.GetCurrentEntryAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    public async Task GetCurrentEntryAsync_CacheMiss_FetchesFromDatabaseAndCaches()
    {
        // Arrange
        var dbEntry = new Entry
        {
            Id = "db-1",
            Type = "sgv",
            Sgv = 120,
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _mockCacheService
            .Setup(x => x.GetAsync<Entry>("entries:current", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        // Production code now uses GetEntriesWithAdvancedFilterAsync with demo mode filter
        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new[] { dbEntry });

        var entryService = new EntryService(
            _mockPostgreSqlService.Object,
            _mockSignalRBroadcastService.Object,
            _mockCacheService.Object,
            _mockCacheConfig.Object,
            _mockDemoModeService.Object,
            _mockEntryDecomposer.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(dbEntry.Id, result.Id);
        Assert.Equal(dbEntry.Sgv, result.Sgv);

        // Verify cache was checked, database was called, and result was cached
        _mockCacheService.Verify(
            x => x.GetAsync<Entry>("entries:current", It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.SetAsync(
                    "entries:current",
                    dbEntry,
                    TimeSpan.FromSeconds(60),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    public async Task CreateEntriesAsync_InvalidatesCurrentEntryCache()
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

        _mockPostgreSqlService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(newEntries);

        var entryService = new EntryService(
            _mockPostgreSqlService.Object,
            _mockSignalRBroadcastService.Object,
            _mockCacheService.Object,
            _mockCacheConfig.Object,
            _mockDemoModeService.Object,
            _mockEntryDecomposer.Object,
            _mockEntryLogger.Object
        );

        // Act
        var result = await entryService.CreateEntriesAsync(newEntries, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);

        // Verify cache was invalidated
        _mockCacheService.Verify(
            x => x.RemoveAsync("entries:current", It.IsAny<CancellationToken>()),
            Times.Once
        );

        // Verify WebSocket broadcast was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync("entries", It.IsAny<object>()),
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system", It.IsAny<CancellationToken>()))
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

        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            _mockDemoModeService.Object,
            dbContext,
            httpContextAccessor,
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
            x => x.GetAsync<StatusResponse>("status:system", It.IsAny<CancellationToken>()),
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system", It.IsAny<CancellationToken>()))
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

        var statusService = new StatusService(
            mockConfiguration,
            _mockCacheService.Object,
            _mockDemoModeService.Object,
            dbContext,
            httpContextAccessor,
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
            x => x.GetAsync<StatusResponse>("status:system", It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.SetAsync(
                    "status:system",
                    It.IsAny<StatusResponse>(),
                    TimeSpan.FromMinutes(2),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }
}
