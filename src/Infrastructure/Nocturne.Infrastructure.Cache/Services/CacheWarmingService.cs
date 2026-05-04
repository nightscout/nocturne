using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;

namespace Nocturne.Infrastructure.Cache.Services;

/// <summary>
/// Service for warming cache with critical data on application startup
/// </summary>
public interface ICacheWarmingService
{
    /// <summary>
    /// Warms essential cache entries for a user
    /// </summary>
    /// <param name="userId">User ID to warm cache for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WarmUserCacheAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Warms system-wide cache entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WarmSystemCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs background cache refresh for frequently accessed items
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RefreshFrequentlyAccessedCacheAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of cache warming service
/// </summary>
public class CacheWarmingService : ICacheWarmingService
{
    private readonly ICacheService _cacheService;
    private readonly IEntryStore _store;
    private readonly ITreatmentService _treatments;
    private readonly ISettingsRepository _settings;
    private readonly CacheConfiguration _config;
    private readonly ILogger<CacheWarmingService> _logger;

    public CacheWarmingService(
        ICacheService cacheService,
        IEntryStore store,
        ITreatmentService treatments,
        ISettingsRepository settings,
        IOptions<CacheConfiguration> config,
        ILogger<CacheWarmingService> logger
    )
    {
        _cacheService = cacheService;
        _store = store;
        _treatments = treatments;
        _settings = settings;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WarmUserCacheAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation("Starting cache warming for user: {UserId}", userId);

            var tasks = new List<Task>();

            // Warm current entry cache
            tasks.Add(WarmCurrentEntryAsync(userId, cancellationToken));

            // Warm recent entries cache
            tasks.Add(WarmRecentEntriesAsync(userId, cancellationToken));

            // Warm recent treatments cache
            tasks.Add(WarmRecentTreatmentsAsync(userId, cancellationToken));

            await Task.WhenAll(tasks);

            _logger.LogInformation("Completed cache warming for user: {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error warming cache for user: {UserId}", userId);
        }
    }

    /// <inheritdoc />
    public async Task WarmSystemCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting system cache warming");

            var tasks = new List<Task>();

            // Warm system lookups and settings
            tasks.Add(WarmSystemLookupsAsync(cancellationToken));

            // Warm system status
            tasks.Add(WarmSystemStatusAsync(cancellationToken));

            await Task.WhenAll(tasks);

            _logger.LogInformation("Completed system cache warming");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error warming system cache");
        }
    }

    /// <inheritdoc />
    public async Task RefreshFrequentlyAccessedCacheAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!_config.EnableBackgroundCacheRefresh)
        {
            _logger.LogDebug("Background cache refresh is disabled");
            return;
        }

        try
        {
            _logger.LogDebug("Starting background cache refresh");

            // Get cache statistics to identify frequently accessed items
            var stats = await _cacheService.GetStatisticsAsync(cancellationToken);

            _logger.LogDebug(
                "Cache statistics: {TotalKeys} keys, {HitRate:P2} hit rate",
                stats.TotalKeys,
                stats.HitRate
            );

            // In a production implementation, you would:
            // 1. Track access patterns for cache keys
            // 2. Identify keys that are frequently accessed and approaching expiration
            // 3. Pre-emptively refresh those keys before they expire

            _logger.LogDebug("Completed background cache refresh");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background cache refresh");
        }
    }

    private async Task WarmCurrentEntryAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var cacheKey = CacheKeyBuilder.BuildCurrentEntriesKey(userId);
            var existsInCache = await _cacheService.ExistsAsync(cacheKey, cancellationToken);

            if (!existsInCache)
            {
                var currentEntry = await _store.GetCurrentAsync(cancellationToken);
                if (currentEntry != null)
                {
                    await _cacheService.SetAsync(
                        cacheKey,
                        currentEntry,
                        TimeSpan.FromSeconds(CacheConstants.Defaults.CurrentEntryExpirationSeconds),
                        cancellationToken
                    );

                    _logger.LogDebug("Warmed current entry cache for user: {UserId}", userId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm current entry cache for user: {UserId}", userId);
        }
    }

    private async Task WarmRecentEntriesAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            // Warm common recent entries queries
            var commonQueries = new[]
            {
                (24, CacheConstants.EntryTypes.Sgv),
                (12, CacheConstants.EntryTypes.Sgv),
                (6, CacheConstants.EntryTypes.Sgv),
            };

            foreach (var (hours, type) in commonQueries)
            {
                var cacheKey = CacheKeyBuilder.BuildRecentEntriesKey(userId, hours, type);
                var existsInCache = await _cacheService.ExistsAsync(cacheKey, cancellationToken);

                if (!existsInCache)
                {
                    var entries = await _store.QueryAsync(
                        new EntryQuery { Type = type, Count = hours },
                        cancellationToken);

                    if (entries.Any())
                    {
                        await _cacheService.SetAsync(
                            cacheKey,
                            entries.ToList(),
                            TimeSpan.FromSeconds(CacheConstants.Defaults.RecentEntriesExpirationSeconds),
                            cancellationToken
                        );
                    }
                }
            }

            _logger.LogDebug("Warmed recent entries cache for user: {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to warm recent entries cache for user: {UserId}",
                userId
            );
        }
    }

    private async Task WarmRecentTreatmentsAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            // Warm common recent treatments queries
            var commonHours = new[] { 24, 12, 48 };

            foreach (var hours in commonHours)
            {
                var cacheKey = CacheKeyBuilder.BuildRecentTreatmentsKey(userId, hours);
                var existsInCache = await _cacheService.ExistsAsync(cacheKey, cancellationToken);

                if (!existsInCache)
                {
                    var treatments = await _treatments.GetTreatmentsAsync(
                        count: 20,
                        skip: 0,
                        cancellationToken: cancellationToken
                    );

                    if (treatments.Any())
                    {
                        await _cacheService.SetAsync(
                            cacheKey,
                            treatments.ToList(),
                            TimeSpan.FromSeconds(CacheConstants.Defaults.RecentTreatmentsExpirationSeconds),
                            cancellationToken
                        );
                    }
                }
            }

            _logger.LogDebug("Warmed recent treatments cache for user: {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to warm recent treatments cache for user: {UserId}",
                userId
            );
        }
    }

    private async Task WarmSystemLookupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cacheKey = $"{CacheConstants.KeyPrefixes.System}:lookups";
            var existsInCache = await _cacheService.ExistsAsync(cacheKey, cancellationToken);

            if (!existsInCache)
            {
                var settings = await _settings.GetSettingsAsync(cancellationToken);
                if (settings.Any())
                {
                    var systemData = new
                    {
                        Settings = settings.ToList(),
                        CachedAt = DateTimeOffset.UtcNow,
                        Status = "ok",
                    };

                    await _cacheService.SetAsync(
                        cacheKey,
                        systemData,
                        CacheConstants.DefaultTtl.SystemLookups,
                        cancellationToken
                    );

                    _logger.LogDebug(
                        "Warmed system lookups cache with {Count} settings",
                        settings.Count()
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm system lookups cache");
        }
    }

    private async Task WarmSystemStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cacheKey = $"{CacheConstants.KeyPrefixes.System}:status";
            var existsInCache = await _cacheService.ExistsAsync(cacheKey, cancellationToken);

            if (!existsInCache)
            {
                var statusData = new
                {
                    Status = "ok",
                    ServerTime = DateTime.UtcNow,
                    CacheWarmed = true,
                };

                await _cacheService.SetAsync(
                    cacheKey,
                    statusData,
                    CacheConstants.DefaultTtl.SystemStatus,
                    cancellationToken
                );

                _logger.LogDebug("Warmed system status cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm system status cache");
        }
    }
}
