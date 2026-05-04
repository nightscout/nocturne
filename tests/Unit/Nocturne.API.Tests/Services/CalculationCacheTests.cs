using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Infrastructure.Cache.Services;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Integration tests for Phase 3 calculation caching features
/// Tests expensive IOB and profile calculations with in-memory caching
/// </summary>
public class Phase3CalculationCacheTests
{
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IIobService> _mockIobService;
    private readonly Mock<IBasalRateResolver> _mockBasalRateResolver;
    private readonly Mock<ISensitivityResolver> _mockSensitivityResolver;
    private readonly Mock<ICarbRatioResolver> _mockCarbRatioResolver;
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettingsResolver;
    private readonly Mock<ITargetRangeResolver> _mockTargetRangeResolver;
    private readonly Mock<IActiveProfileResolver> _mockActiveProfileResolver;
    private readonly Mock<ILogger<CachedIobService>> _mockIobLogger;
    private readonly Mock<ILogger<CachedProfileService>> _mockProfileLogger;
    private readonly Mock<ILogger<CacheInvalidationService>> _mockInvalidationLogger;
    private readonly IOptions<CalculationCacheConfiguration> _cacheConfig;

    public Phase3CalculationCacheTests()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockIobService = new Mock<IIobService>();
        _mockBasalRateResolver = new Mock<IBasalRateResolver>();
        _mockSensitivityResolver = new Mock<ISensitivityResolver>();
        _mockCarbRatioResolver = new Mock<ICarbRatioResolver>();
        _mockTherapySettingsResolver = new Mock<ITherapySettingsResolver>();
        _mockTargetRangeResolver = new Mock<ITargetRangeResolver>();
        _mockActiveProfileResolver = new Mock<IActiveProfileResolver>();
        _mockIobLogger = new Mock<ILogger<CachedIobService>>();
        _mockProfileLogger = new Mock<ILogger<CachedProfileService>>();
        _mockInvalidationLogger = new Mock<ILogger<CacheInvalidationService>>();

        _cacheConfig = Options.Create(
            new CalculationCacheConfiguration
            {
                IobCalculationExpirationSeconds = 900,
                CobCalculationExpirationSeconds = 900,
                ProfileCalculationExpirationSeconds = 3600,
                StatisticsExpirationSeconds = 1800,
            }
        );
    }

    #region IOB Calculation Caching Tests

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    [Trait("Category", "IOB")]
    public async Task CachedIobService_CalculateTotalAsync_CacheHit_ReturnsCachedResult()
    {
        // Arrange
        var userId = "test-user";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                EnteredBy = userId,
                Insulin = 5.0,
                Mills = timestamp - 60000,
            },
        };
        var expectedIobResult = new IobResult
        {
            Iob = 2.5,
            Activity = 0.1,
            Source = "Care Portal",
        };

        var expectedCacheKey = CacheKeyBuilder.BuildIobCalculationKey(userId, timestamp);
        _mockCacheService
            .Setup(x =>
                x.GetOrSetAsync(
                    expectedCacheKey,
                    It.IsAny<Func<Task<IobResult>>>(),
                    TimeSpan.FromSeconds(900),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedIobResult);

        var cachedIobService = new CachedIobService(
            _mockIobService.Object,
            _mockCacheService.Object,
            _cacheConfig,
            _mockIobLogger.Object
        );

        // Act
        var result = await cachedIobService.CalculateTotalAsync(
            treatments,
            timestamp,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedIobResult.Iob, result.Iob);
        Assert.Equal(expectedIobResult.Activity, result.Activity);
        Assert.Equal(expectedIobResult.Source, result.Source);

        _mockCacheService.Verify(
            x =>
                x.GetOrSetAsync(
                    expectedCacheKey,
                    It.IsAny<Func<Task<IobResult>>>(),
                    TimeSpan.FromSeconds(900),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    [Trait("Category", "IOB")]
    public async Task CachedIobService_InvalidateIobCache_RemovesCorrectPattern()
    {
        // Arrange
        var userId = "test-user";
        var expectedPattern = CacheKeyBuilder.BuildIobCalculationPattern(userId);

        var cachedIobService = new CachedIobService(
            _mockIobService.Object,
            _mockCacheService.Object,
            _cacheConfig,
            _mockIobLogger.Object
        );

        // Act
        await cachedIobService.InvalidateIobCacheAsync(userId, CancellationToken.None);

        // Assert
        _mockCacheService.Verify(
            x => x.RemoveByPatternAsync(expectedPattern, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    #endregion

    #region Profile Calculation Caching Tests

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    [Trait("Category", "Profile")]
    public async Task CachedProfileService_GetProfileCalculationsAsync_CacheHit_ReturnsCachedResult()
    {
        // Arrange
        var profileId = "test-profile";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var expectedProfileResult = new ProfileCalculationResult
        {
            BasalRate = 1.2,
            Sensitivity = 50.0,
            CarbRatio = 10.0,
            DIA = 3.0,
            Timestamp = timestamp,
            ProfileName = "Default",
        };

        var expectedCacheKey = CacheKeyBuilder.BuildProfileCalculatedKey(profileId, timestamp);
        _mockCacheService
            .Setup(x =>
                x.GetOrSetAsync(
                    expectedCacheKey,
                    It.IsAny<Func<Task<ProfileCalculationResult>>>(),
                    TimeSpan.FromSeconds(3600),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedProfileResult);

        var cachedProfileService = new CachedProfileService(
            _mockBasalRateResolver.Object,
            _mockSensitivityResolver.Object,
            _mockCarbRatioResolver.Object,
            _mockTherapySettingsResolver.Object,
            _mockTargetRangeResolver.Object,
            _mockActiveProfileResolver.Object,
            _mockCacheService.Object,
            _cacheConfig,
            _mockProfileLogger.Object
        );

        // Act
        var result = await cachedProfileService.GetProfileCalculationsAsync(
            profileId,
            timestamp,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedProfileResult.BasalRate, result.BasalRate);
        Assert.Equal(expectedProfileResult.Sensitivity, result.Sensitivity);
        Assert.Equal(expectedProfileResult.CarbRatio, result.CarbRatio);
        Assert.Equal(expectedProfileResult.DIA, result.DIA);
        Assert.Equal(expectedProfileResult.Timestamp, result.Timestamp);
        Assert.Equal(expectedProfileResult.ProfileName, result.ProfileName);

        _mockCacheService.Verify(
            x =>
                x.GetOrSetAsync(
                    expectedCacheKey,
                    It.IsAny<Func<Task<ProfileCalculationResult>>>(),
                    TimeSpan.FromSeconds(3600),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Cache Invalidation Tests

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    [Trait("Category", "Invalidation")]
    public async Task CacheInvalidationService_InvalidateForNewInsulinTreatment_InvalidatesCorrectPatterns()
    {
        // Arrange
        var userId = "test-user";
        var invalidationService = new CacheInvalidationService(
            _mockCacheService.Object,
            _mockInvalidationLogger.Object
        );

        // Act
        await invalidationService.InvalidateForNewInsulinTreatmentAsync(
            userId,
            CancellationToken.None
        );

        // Assert
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentTreatmentsPattern(userId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildIobCalculationPattern(userId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildStatsPattern(userId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Cache")]
    [Trait("Category", "Invalidation")]
    public async Task CacheInvalidationService_InvalidateForNewGlucoseEntry_InvalidatesCorrectPatterns()
    {
        // Arrange
        var userId = "test-user";
        var invalidationService = new CacheInvalidationService(
            _mockCacheService.Object,
            _mockInvalidationLogger.Object
        );

        // Act
        await invalidationService.InvalidateForNewGlucoseEntryAsync(userId, CancellationToken.None);

        // Assert
        _mockCacheService.Verify(
            x =>
                x.RemoveAsync(
                    CacheKeyBuilder.BuildCurrentEntriesKey(userId, null),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentEntriesPattern(userId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildPattern("stats", userId, "glucose:*"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildPattern("stats", userId, "tir:*"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildPattern("stats", userId, "hba1c:*"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Cache Key Builder Tests

    [Theory]
    [InlineData("user123", 1640995200000L)] // 2022-01-01 00:00:00 UTC
    [InlineData("user456", 1641081600000L)] // 2022-01-02 00:00:00 UTC
    [Trait("Category", "Unit")]
    [Trait("Category", "CacheKeys")]
    public void CacheKeyBuilder_BuildIobCalculationKey_GeneratesCorrectKey(
        string userId,
        long timestamp
    )
    {
        // Act
        var key = CacheKeyBuilder.BuildIobCalculationKey(userId, timestamp);

        // Assert
        var expected = $"calculations:iob:{userId}:{timestamp}";
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("user123", 1640995200000L)] // 2022-01-01 00:00:00 UTC
    [InlineData("user456", 1641081600000L)] // 2022-01-02 00:00:00 UTC
    [Trait("Category", "Unit")]
    [Trait("Category", "CacheKeys")]
    public void CacheKeyBuilder_BuildCobCalculationKey_GeneratesCorrectKey(
        string userId,
        long timestamp
    )
    {
        // Act
        var key = CacheKeyBuilder.BuildCobCalculationKey(userId, timestamp);

        // Assert
        var expected = $"calculations:cob:{userId}:{timestamp}";
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("profile123", 1640995200000L)] // 2022-01-01 00:00:00 UTC
    [InlineData("profile456", 1641081600000L)] // 2022-01-02 00:00:00 UTC
    [Trait("Category", "Unit")]
    [Trait("Category", "CacheKeys")]
    public void CacheKeyBuilder_BuildProfileCalculatedKey_GeneratesCorrectKey(
        string profileId,
        long timestamp
    )
    {
        // Act
        var key = CacheKeyBuilder.BuildProfileCalculatedKey(profileId, timestamp);

        // Assert
        var expected = $"profiles:calculated:{profileId}:{timestamp}";
        Assert.Equal(expected, key);
    }

    #endregion
}
