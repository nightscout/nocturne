using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Keys;

namespace Nocturne.Infrastructure.Cache.Services;

/// <summary>
/// Configuration for Phase 3 calculation caching TTLs
/// </summary>
public class CalculationCacheConfiguration
{
    /// <summary>
    /// IOB calculation cache expiration in seconds (default: 15 minutes)
    /// </summary>
    public int IobCalculationExpirationSeconds { get; set; } = 900; // 15 minutes

    /// <summary>
    /// COB calculation cache expiration in seconds (default: 15 minutes)
    /// </summary>
    public int CobCalculationExpirationSeconds { get; set; } = 900; // 15 minutes

    /// <summary>
    /// Profile calculation cache expiration in seconds (default: 1 hour)
    /// </summary>
    public int ProfileCalculationExpirationSeconds { get; set; } = 3600; // 1 hour

    /// <summary>
    /// Statistics cache expiration in seconds (default: 30 minutes)
    /// </summary>
    public int StatisticsExpirationSeconds { get; set; } = 1800; // 30 minutes
}

/// <summary>
/// Cached wrapper for IOB calculations implementing Phase 3 caching strategy
/// </summary>
public interface ICachedIobService
{
    /// <summary>
    /// Calculate total IOB with caching
    /// </summary>
    Task<IobResult> CalculateTotalAsync(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate IOB cache for specific user
    /// </summary>
    Task InvalidateIobCacheAsync(string userId, CancellationToken cancellationToken = default);

    // Forward synchronous methods to underlying service
    IobResult CalculateTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    );
}

/// <summary>
/// Cached IOB service implementation with in-memory caching for expensive calculations
/// </summary>
public class CachedIobService : ICachedIobService
{
    private readonly IIobService _iobService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CachedIobService> _logger;
    private readonly CalculationCacheConfiguration _config;

    public CachedIobService(
        IIobService iobService,
        ICacheService cacheService,
        IOptions<CalculationCacheConfiguration> config,
        ILogger<CachedIobService> logger
    )
    {
        _iobService = iobService;
        _cacheService = cacheService;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IobResult> CalculateTotalAsync(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null,
        CancellationToken cancellationToken = default
    )
    {
        var timestamp = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userId = ExtractUserId(treatments, deviceStatus);

        if (string.IsNullOrEmpty(userId))
        {
            // No user ID available, skip caching
            return _iobService.CalculateTotal(treatments, deviceStatus, time, specProfile);
        }

        var cacheKey = CacheKeyBuilder.BuildIobCalculationKey(userId, timestamp);
        var expiration = TimeSpan.FromSeconds(_config.IobCalculationExpirationSeconds);

        return await _cacheService.GetOrSetAsync(
            cacheKey,
            () =>
                Task.FromResult(
                    _iobService.CalculateTotal(treatments, deviceStatus, time, specProfile)
                ),
            expiration,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public IobResult CalculateTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    )
    {
        return CalculateTotalAsync(treatments, deviceStatus, time, specProfile)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public async Task InvalidateIobCacheAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var pattern = CacheKeyBuilder.BuildIobCalculationPattern(userId);
            await _cacheService.RemoveByPatternAsync(pattern, cancellationToken);
            _logger.LogDebug("Invalidated IOB cache for user: {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating IOB cache for user: {UserId}", userId);
        }
    }

    /// <summary>
    /// Extracts user ID from treatments or device status for cache key generation
    /// </summary>
    private static string? ExtractUserId(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus
    )
    {
        // Try to extract from treatments first
        var userIdFromTreatment = treatments?.FirstOrDefault()?.EnteredBy;
        if (!string.IsNullOrEmpty(userIdFromTreatment))
        {
            return userIdFromTreatment;
        }

        // Try to extract from device status
        var userIdFromDevice = deviceStatus?.FirstOrDefault()?.Device;
        if (!string.IsNullOrEmpty(userIdFromDevice))
        {
            return userIdFromDevice;
        }

        // Could not determine user ID
        return null;
    }
}

/// <summary>
/// Cached wrapper for Profile calculations implementing Phase 3 caching strategy
/// </summary>
public interface ICachedProfileService
{
    /// <summary>
    /// Get profile values at timestamp with caching
    /// </summary>
    Task<ProfileCalculationResult> GetProfileCalculationsAsync(
        string profileId,
        long timestamp,
        string? specProfile = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate profile calculation cache
    /// </summary>
    Task InvalidateProfileCalculationCacheAsync(
        string profileId,
        CancellationToken cancellationToken = default
    );

    // Forward other methods to underlying service
    double GetBasalRate(long time, string? specProfile = null);
    double GetSensitivity(long time, string? specProfile = null);
    double GetCarbRatio(long time, string? specProfile = null);
    double GetDIA(long time, string? specProfile = null);
}

/// <summary>
/// Result object for cached profile calculations at a specific timestamp
/// </summary>
public class ProfileCalculationResult
{
    public double BasalRate { get; set; }
    public double Sensitivity { get; set; }
    public double CarbRatio { get; set; }
    public double CarbAbsorptionRate { get; set; }
    public double DIA { get; set; }
    public double LowBGTarget { get; set; }
    public double HighBGTarget { get; set; }
    public long Timestamp { get; set; }
    public string? ProfileName { get; set; }
}

/// <summary>
/// Cached profile service implementation with in-memory caching for expensive calculations
/// </summary>
public class CachedProfileService : ICachedProfileService
{
    private readonly IProfileService _profileService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CachedProfileService> _logger;
    private readonly CalculationCacheConfiguration _config;

    public CachedProfileService(
        IProfileService profileService,
        ICacheService cacheService,
        IOptions<CalculationCacheConfiguration> config,
        ILogger<CachedProfileService> logger
    )
    {
        _profileService = profileService;
        _cacheService = cacheService;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProfileCalculationResult> GetProfileCalculationsAsync(
        string profileId,
        long timestamp,
        string? specProfile = null,
        CancellationToken cancellationToken = default
    )
    {
        var cacheKey = CacheKeyBuilder.BuildProfileCalculatedKey(profileId, timestamp);
        var expiration = TimeSpan.FromSeconds(_config.ProfileCalculationExpirationSeconds);

        return await _cacheService.GetOrSetAsync(
            cacheKey,
            () =>
                Task.FromResult(
                    new ProfileCalculationResult
                    {
                        BasalRate = _profileService.GetBasalRate(timestamp, specProfile),
                        Sensitivity = _profileService.GetSensitivity(timestamp, specProfile),
                        CarbRatio = _profileService.GetCarbRatio(timestamp, specProfile),
                        CarbAbsorptionRate = _profileService.GetCarbAbsorptionRate(
                            timestamp,
                            specProfile
                        ),
                        DIA = _profileService.GetDIA(timestamp, specProfile),
                        LowBGTarget = _profileService.GetLowBGTarget(timestamp, specProfile),
                        HighBGTarget = _profileService.GetHighBGTarget(timestamp, specProfile),
                        Timestamp = timestamp,
                        ProfileName =
                            specProfile ?? _profileService.GetActiveProfileName(timestamp),
                    }
                ),
            expiration,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task InvalidateProfileCalculationCacheAsync(
        string profileId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var pattern = CacheKeyBuilder.BuildProfileCalculatedPattern(profileId);
            await _cacheService.RemoveByPatternAsync(pattern, cancellationToken);
            _logger.LogDebug(
                "Invalidated profile calculation cache for profile: {ProfileId}",
                profileId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invalidating profile calculation cache for profile: {ProfileId}",
                profileId
            );
        }
    }

    // Forward methods to underlying service without caching
    /// <inheritdoc />
    public double GetBasalRate(long time, string? specProfile = null) =>
        _profileService.GetBasalRate(time, specProfile);

    /// <inheritdoc />
    public double GetSensitivity(long time, string? specProfile = null) =>
        _profileService.GetSensitivity(time, specProfile);

    /// <inheritdoc />
    public double GetCarbRatio(long time, string? specProfile = null) =>
        _profileService.GetCarbRatio(time, specProfile);

    /// <inheritdoc />
    public double GetDIA(long time, string? specProfile = null) =>
        _profileService.GetDIA(time, specProfile);
}
