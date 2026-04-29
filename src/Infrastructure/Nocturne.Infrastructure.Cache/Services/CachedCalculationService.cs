using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
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
/// Cached wrapper for IOB calculations implementing Phase 3 caching strategy.
/// </summary>
public interface ICachedIobService
{
    /// <summary>
    /// Calculate total IOB with caching.
    /// </summary>
    Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate IOB cache for specific user.
    /// </summary>
    Task InvalidateIobCacheAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cached IOB service implementation with in-memory caching for expensive calculations.
/// Uses tenant ID from the request scope for cache key isolation.
/// </summary>
public class CachedIobService : ICachedIobService
{
    private readonly IIobCalculator _iobCalculator;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CachedIobService> _logger;
    private readonly CalculationCacheConfiguration _config;

    public CachedIobService(
        IIobCalculator iobCalculator,
        ITenantAccessor tenantAccessor,
        ICacheService cacheService,
        IOptions<CalculationCacheConfiguration> config,
        ILogger<CachedIobService> logger
    )
    {
        _iobCalculator = iobCalculator;
        _tenantAccessor = tenantAccessor;
        _cacheService = cacheService;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken cancellationToken = default
    )
    {
        var timestamp = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tenantId = _tenantAccessor.Context?.TenantId.ToString();

        if (string.IsNullOrEmpty(tenantId))
        {
            // No tenant resolved, skip caching
            return await _iobCalculator.CalculateTotalAsync(boluses, tempBasals, time, cancellationToken);
        }

        var cacheKey = CacheKeyBuilder.BuildIobCalculationKey(tenantId, timestamp);
        var expiration = TimeSpan.FromSeconds(_config.IobCalculationExpirationSeconds);

        return await _cacheService.GetOrSetAsync(
            cacheKey,
            () => _iobCalculator.CalculateTotalAsync(boluses, tempBasals, time, cancellationToken),
            expiration,
            cancellationToken
        );
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
}

/// <summary>
/// Cached wrapper for Profile calculations implementing Phase 3 caching strategy.
/// Delegates to the V4 resolver interfaces for actual value resolution.
/// </summary>
public interface ICachedProfileService
{
    /// <summary>
    /// Get profile values at timestamp with caching.
    /// </summary>
    Task<ProfileCalculationResult> GetProfileCalculationsAsync(
        string profileId,
        long timestamp,
        string? specProfile = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate profile calculation cache.
    /// </summary>
    Task InvalidateProfileCalculationCacheAsync(
        string profileId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get scheduled basal rate (U/hr) at a given time.
    /// </summary>
    Task<double> GetBasalRateAsync(long time, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Get insulin sensitivity factor (mg/dL per U) at a given time.
    /// </summary>
    Task<double> GetSensitivityAsync(long time, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Get carb ratio (g/U) at a given time.
    /// </summary>
    Task<double> GetCarbRatioAsync(long time, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Get Duration of Insulin Action in hours at a given time.
    /// </summary>
    Task<double> GetDIAAsync(long time, string? specProfile = null, CancellationToken ct = default);
}

/// <summary>
/// Result object for cached profile calculations at a specific timestamp.
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
/// Cached profile service implementation that delegates to V4 resolver interfaces
/// and caches the results.
/// </summary>
public class CachedProfileService : ICachedProfileService
{
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ISensitivityResolver _sensitivityResolver;
    private readonly ICarbRatioResolver _carbRatioResolver;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CachedProfileService> _logger;
    private readonly CalculationCacheConfiguration _config;

    public CachedProfileService(
        IBasalRateResolver basalRateResolver,
        ISensitivityResolver sensitivityResolver,
        ICarbRatioResolver carbRatioResolver,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver,
        IActiveProfileResolver activeProfileResolver,
        ICacheService cacheService,
        IOptions<CalculationCacheConfiguration> config,
        ILogger<CachedProfileService> logger
    )
    {
        _basalRateResolver = basalRateResolver;
        _sensitivityResolver = sensitivityResolver;
        _carbRatioResolver = carbRatioResolver;
        _therapySettingsResolver = therapySettingsResolver;
        _targetRangeResolver = targetRangeResolver;
        _activeProfileResolver = activeProfileResolver;
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
            async () =>
                new ProfileCalculationResult
                {
                    BasalRate = await _basalRateResolver.GetBasalRateAsync(timestamp, specProfile, cancellationToken),
                    Sensitivity = await _sensitivityResolver.GetSensitivityAsync(timestamp, specProfile, cancellationToken),
                    CarbRatio = await _carbRatioResolver.GetCarbRatioAsync(timestamp, specProfile, cancellationToken),
                    CarbAbsorptionRate = await _therapySettingsResolver.GetCarbAbsorptionRateAsync(timestamp, specProfile, cancellationToken),
                    DIA = await _therapySettingsResolver.GetDIAAsync(timestamp, specProfile, cancellationToken),
                    LowBGTarget = await _targetRangeResolver.GetLowBGTargetAsync(timestamp, specProfile, cancellationToken),
                    HighBGTarget = await _targetRangeResolver.GetHighBGTargetAsync(timestamp, specProfile, cancellationToken),
                    Timestamp = timestamp,
                    ProfileName = specProfile ?? await _activeProfileResolver.GetActiveProfileNameAsync(timestamp, cancellationToken),
                },
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

    /// <inheritdoc />
    public Task<double> GetBasalRateAsync(long time, string? specProfile = null, CancellationToken ct = default) =>
        _basalRateResolver.GetBasalRateAsync(time, specProfile, ct);

    /// <inheritdoc />
    public Task<double> GetSensitivityAsync(long time, string? specProfile = null, CancellationToken ct = default) =>
        _sensitivityResolver.GetSensitivityAsync(time, specProfile, ct);

    /// <inheritdoc />
    public Task<double> GetCarbRatioAsync(long time, string? specProfile = null, CancellationToken ct = default) =>
        _carbRatioResolver.GetCarbRatioAsync(time, specProfile, ct);

    /// <inheritdoc />
    public Task<double> GetDIAAsync(long time, string? specProfile = null, CancellationToken ct = default) =>
        _therapySettingsResolver.GetDIAAsync(time, specProfile, ct);
}
