using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Infrastructure.Cache.Services;

namespace Nocturne.Infrastructure.Cache.Services;

/// <summary>
/// Removes the cache entries invalidated by a new treatment, entry, or profile change.
/// </summary>
public interface ICacheInvalidationService
{
    /// <summary>
    /// Invalidate cache when new insulin treatment is added
    /// Invalidates: treatments:recent:*, calculations:iob:*
    /// </summary>
    Task InvalidateForNewInsulinTreatmentAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate cache when new carb treatment is added
    /// Invalidates: treatments:recent:*
    /// </summary>
    Task InvalidateForNewCarbTreatmentAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate cache when new glucose entry is added
    /// Invalidates: entries:current, entries:recent:*
    /// </summary>
    Task InvalidateForNewGlucoseEntryAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate cache when profile changes
    /// Invalidates: profiles:*, calculations:iob:*
    /// </summary>
    Task InvalidateForProfileChangeAsync(
        string userId,
        string? profileId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invalidate all calculation caches for a user (nuclear option)
    /// </summary>
    Task InvalidateAllCalculationsAsync(
        string userId,
        CancellationToken cancellationToken = default
    );
}

public class CacheInvalidationService : ICacheInvalidationService
{
    private readonly ICacheService _cacheService;
    private readonly ILogger<CacheInvalidationService> _logger;

    public CacheInvalidationService(
        ICacheService cacheService,
        ILogger<CacheInvalidationService> logger
    )
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InvalidateForNewInsulinTreatmentAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Starting cache invalidation for new insulin treatment, user: {UserId}",
                userId
            );

            // Invalidation chain for new insulin treatment:
            // - treatments:recent:* (recent treatments cache)
            // - calculations:iob:* (all IOB calculations)

            var invalidationTasks = new List<Task>
            {
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentTreatmentsPattern(userId),
                    cancellationToken
                ),
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildIobCalculationPattern(userId),
                    cancellationToken
                ),
            };

            await Task.WhenAll(invalidationTasks);

            _logger.LogInformation(
                "Completed cache invalidation for new insulin treatment, user: {UserId}",
                userId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invalidating cache for new insulin treatment, user: {UserId}",
                userId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InvalidateForNewCarbTreatmentAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Starting cache invalidation for new carb treatment, user: {UserId}",
                userId
            );

            // Invalidation chain for new carb treatment:
            // - treatments:recent:* (recent treatments cache)

            var invalidationTasks = new List<Task>
            {
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentTreatmentsPattern(userId),
                    cancellationToken
                ),
            };

            await Task.WhenAll(invalidationTasks);

            _logger.LogInformation(
                "Completed cache invalidation for new carb treatment, user: {UserId}",
                userId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invalidating cache for new carb treatment, user: {UserId}",
                userId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InvalidateForNewGlucoseEntryAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Starting cache invalidation for new glucose entry, user: {UserId}",
                userId
            );

            // Invalidation chain for new glucose entry:
            // - entries:current (current entries cache)
            // - entries:recent:* (recent entries cache)

            var invalidationTasks = new List<Task>
            {
                _cacheService.RemoveAsync(
                    CacheKeyBuilder.BuildCurrentEntriesKey(userId),
                    cancellationToken
                ),
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentEntriesPattern(userId),
                    cancellationToken
                ),
            };

            await Task.WhenAll(invalidationTasks);

            _logger.LogInformation(
                "Completed cache invalidation for new glucose entry, user: {UserId}",
                userId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invalidating cache for new glucose entry, user: {UserId}",
                userId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InvalidateForProfileChangeAsync(
        string userId,
        string? profileId = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Starting cache invalidation for profile change, user: {UserId}, profileId: {ProfileId}",
                userId,
                profileId
            );

            // Invalidation chain for profile change:
            // - profiles:* (all profile caches)
            // - calculations:iob:* (basal rates affect IOB)

            var invalidationTasks = new List<Task>
            {
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildPattern("profiles", userId, "*"),
                    cancellationToken
                ),
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildIobCalculationPattern(userId),
                    cancellationToken
                ),
            };

            // If specific profile ID is provided, also invalidate profile-specific calculated cache
            if (!string.IsNullOrEmpty(profileId))
            {
                invalidationTasks.Add(
                    _cacheService.RemoveByPatternAsync(
                        CacheKeyBuilder.BuildProfileCalculatedPattern(profileId),
                        cancellationToken
                    )
                );
            }

            await Task.WhenAll(invalidationTasks);

            _logger.LogInformation(
                "Completed cache invalidation for profile change, user: {UserId}, profileId: {ProfileId}",
                userId,
                profileId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invalidating cache for profile change, user: {UserId}, profileId: {ProfileId}",
                userId,
                profileId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAllCalculationsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogWarning(
                "Starting nuclear cache invalidation for all calculations, user: {UserId}",
                userId
            );

            // Nuclear option: invalidate all caches for user
            var invalidationTasks = new List<Task>
            {
                // Entries and treatments
                _cacheService.RemoveAsync(
                    CacheKeyBuilder.BuildCurrentEntriesKey(userId),
                    cancellationToken
                ),
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentEntriesPattern(userId),
                    cancellationToken
                ),
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildRecentTreatmentsPattern(userId),
                    cancellationToken
                ),
                // Profiles
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildPattern("profiles", userId, "*"),
                    cancellationToken
                ),
                // Calculations
                _cacheService.RemoveByPatternAsync(
                    CacheKeyBuilder.BuildIobCalculationPattern(userId),
                    cancellationToken
                ),
            };

            await Task.WhenAll(invalidationTasks);

            _logger.LogWarning(
                "Completed nuclear cache invalidation for all calculations, user: {UserId}",
                userId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during nuclear cache invalidation, user: {UserId}", userId);
            throw;
        }
    }
}
