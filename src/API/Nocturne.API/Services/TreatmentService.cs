using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for treatment operations with WebSocket broadcasting
/// </summary>
public class TreatmentService : ITreatmentService
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly ICacheService _cacheService;
    private readonly CacheConfiguration _cacheConfig;
    private readonly IDemoModeService _demoModeService;
    private readonly ILogger<TreatmentService> _logger;
    private const string CollectionName = "treatments";
    private const string DefaultTenantId = "default"; // TODO: Replace with actual tenant context

    public TreatmentService(
        IPostgreSqlService postgreSqlService,
        ISignalRBroadcastService broadcastService,
        ICacheService cacheService,
        IOptions<CacheConfiguration> cacheConfig,
        IDemoModeService demoModeService,
        ILogger<TreatmentService> logger
    )
    {
        _postgreSqlService = postgreSqlService;
        _broadcastService = broadcastService;
        _cacheService = cacheService;
        _cacheConfig = cacheConfig.Value;
        _demoModeService = demoModeService;
        _logger = logger;
    }

    /// <summary>
    /// Builds a find query that filters by demo mode.
    /// This ensures filtering happens at the database level for efficiency.
    /// </summary>
    /// <param name="existingQuery">Optional existing query to merge with</param>
    /// <returns>A JSON find query string with data_source filter</returns>
    private string BuildDemoModeFilterQuery(string? existingQuery = null)
    {
        // Check if existing query is JSON
        bool isJson =
            !string.IsNullOrWhiteSpace(existingQuery)
            && existingQuery.Trim().StartsWith("{")
            && existingQuery.Trim().EndsWith("}");

        if (isJson)
        {
            // JSON Logic
            string demoFilter;
            if (_demoModeService.IsEnabled)
            {
                demoFilter = $"\"data_source\":\"{Core.Constants.DataSources.DemoService}\"";
            }
            else
            {
                demoFilter =
                    $"\"data_source\":{{\"$ne\":\"{Core.Constants.DataSources.DemoService}\"}}";
            }

            if (string.IsNullOrWhiteSpace(existingQuery) || existingQuery == "{}")
            {
                return "{" + demoFilter + "}";
            }

            var trimmed = existingQuery!.Trim();
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return string.IsNullOrEmpty(inner)
                ? "{" + demoFilter + "}"
                : "{" + demoFilter + "," + inner + "}";
        }
        else
        {
            // URL Params Logic
            var demoParamResponse = "";
            if (_demoModeService.IsEnabled)
            {
                // find[data_source]=demo-service
                demoParamResponse = $"find[data_source]={Core.Constants.DataSources.DemoService}";
            }
            else
            {
                // find[data_source][$ne]=demo-service
                demoParamResponse =
                    $"find[data_source][$ne]={Core.Constants.DataSources.DemoService}";
            }

            if (string.IsNullOrWhiteSpace(existingQuery))
            {
                return "{"
                    + (
                        _demoModeService.IsEnabled
                            ? $"\"data_source\":\"{Core.Constants.DataSources.DemoService}\""
                            : $"\"data_source\":{{\"$ne\":\"{Core.Constants.DataSources.DemoService}\"}}"
                    )
                    + "}";
            }

            // Append to existing URL params
            return $"{existingQuery}&{demoParamResponse}";
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    )
    {
        var actualCount = count ?? 10;
        var actualSkip = skip ?? 0;

        // Build query with demo mode filter at database level
        var findQuery = BuildDemoModeFilterQuery(find);

        // If find query is provided, use advanced filtering (no caching for filtered queries)
        if (!string.IsNullOrEmpty(find))
        {
            _logger.LogDebug(
                "Using advanced filter for treatments with findQuery: {FindQuery}, count: {Count}, skip: {Skip}, demoMode: {DemoMode}",
                findQuery,
                actualCount,
                actualSkip,
                _demoModeService.IsEnabled
            );
            var treatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
                count: actualCount,
                skip: actualSkip,
                findQuery: findQuery,
                reverseResults: false,
                cancellationToken: cancellationToken
            );
            return treatments;
        }

        // Cache recent treatments for common queries (skip = 0 and common counts)
        // Include demo mode in cache key to avoid mixing demo/non-demo data
        if (actualSkip == 0 && IsCommonTreatmentCount(actualCount))
        {
            // Determine time range based on common patterns (default to 24 hours for treatments)
            var hours = DetermineTimeRangeHours(actualCount);
            var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
            var cacheKey =
                CacheKeyBuilder.BuildRecentTreatmentsKey(DefaultTenantId, hours, actualCount)
                + demoSuffix;
            var cacheTtl = TimeSpan.FromSeconds(
                CacheConstants.Defaults.RecentTreatmentsExpirationSeconds
            );

            return await _cacheService.GetOrSetAsync(
                cacheKey,
                async () =>
                {
                    _logger.LogDebug(
                        "Cache MISS for recent treatments (count: {Count}, hours: {Hours}, demoMode: {DemoMode}), fetching from database with filter: {Filter}",
                        actualCount,
                        hours,
                        _demoModeService.IsEnabled,
                        findQuery
                    );
                    var treatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
                        count: actualCount,
                        skip: actualSkip,
                        findQuery: findQuery,
                        reverseResults: false,
                        cancellationToken: cancellationToken
                    );
                    return treatments.ToList();
                },
                cacheTtl,
                cancellationToken
            );
        }

        // Non-cached path for non-standard queries
        var allTreatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
            count: actualCount,
            skip: actualSkip,
            findQuery: findQuery,
            reverseResults: false,
            cancellationToken: cancellationToken
        );
        return allTreatments;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Build query with demo mode filter at database level
        var findQuery = BuildDemoModeFilterQuery(null);

        // Cache recent treatments for common queries (skip = 0 and common counts)
        // Include demo mode in cache key to avoid mixing demo/non-demo data
        if (skip == 0 && IsCommonTreatmentCount(count))
        {
            // Determine time range based on common patterns (default to 24 hours for treatments)
            var hours = DetermineTimeRangeHours(count);
            var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
            var cacheKey =
                CacheKeyBuilder.BuildRecentTreatmentsKey(DefaultTenantId, hours, count)
                + demoSuffix;
            var cacheTtl = TimeSpan.FromSeconds(
                CacheConstants.Defaults.RecentTreatmentsExpirationSeconds
            );

            return await _cacheService.GetOrSetAsync(
                cacheKey,
                async () =>
                {
                    _logger.LogDebug(
                        "Cache MISS for recent treatments (count: {Count}, hours: {Hours}, demoMode: {DemoMode}), fetching from database with filter: {Filter}",
                        count,
                        hours,
                        _demoModeService.IsEnabled,
                        findQuery
                    );
                    var treatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
                        count: count,
                        skip: skip,
                        findQuery: findQuery,
                        reverseResults: false,
                        cancellationToken: cancellationToken
                    );
                    return treatments.ToList();
                },
                cacheTtl,
                cancellationToken
            );
        }

        // Non-cached path for non-standard queries
        var allTreatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
            count: count,
            skip: skip,
            findQuery: findQuery,
            reverseResults: false,
            cancellationToken: cancellationToken
        );
        return allTreatments;
    }

    /// <summary>
    /// Determines if the treatment count is common enough to cache
    /// </summary>
    /// <param name="count">The count to check</param>
    /// <returns>True if the count is common (10, 50, 100), false otherwise</returns>
    private static bool IsCommonTreatmentCount(int count)
    {
        return count is 10 or 50 or 100;
    }

    /// <summary>
    /// Determines the appropriate time range hours based on treatment count
    /// </summary>
    /// <param name="count">The treatment count</param>
    /// <returns>Time range in hours (12, 24, or 48)</returns>
    private static int DetermineTimeRangeHours(int count)
    {
        return count switch
        {
            <= 10 => 12, // 12 hours for small counts
            <= 50 => 24, // 24 hours for medium counts
            _ => 48, // 48 hours for large counts
        };
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetTreatmentByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        return await _postgreSqlService.GetTreatmentByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    )
    {
        var createdTreatments = await _postgreSqlService.CreateTreatmentsAsync(
            treatments,
            cancellationToken
        );

        // Invalidate all recent treatments caches since new treatments were created
        try
        {
            var recentTreatmentsPattern = CacheKeyBuilder.BuildRecentTreatmentsPattern(
                DefaultTenantId
            );
            await _cacheService.RemoveByPatternAsync(recentTreatmentsPattern, cancellationToken);
            _logger.LogInformation(
                "Cache INVALIDATION: recent treatments pattern '{Pattern}' after creating {Count} treatments",
                recentTreatmentsPattern,
                createdTreatments.Count()
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate treatment caches");
        }

        // Broadcast create events for each treatment (replaces legacy ctx.bus.emit('storage-socket-create'))
        foreach (var treatment in createdTreatments)
        {
            try
            {
                await _broadcastService.BroadcastStorageCreateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = treatment }
                );
                _logger.LogDebug(
                    "Broadcasted storage create event for treatment {TreatmentId}",
                    treatment.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage create event for treatment {TreatmentId}",
                    treatment.Id
                );
            }
        }

        return createdTreatments;
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateTreatmentAsync(
        string id,
        Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        var updatedTreatment = await _postgreSqlService.UpdateTreatmentAsync(
            id,
            treatment,
            cancellationToken
        );

        if (updatedTreatment != null)
        {
            // Invalidate all recent treatments caches since a treatment was updated
            try
            {
                var recentTreatmentsPattern = CacheKeyBuilder.BuildRecentTreatmentsPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(
                    recentTreatmentsPattern,
                    cancellationToken
                );
                _logger.LogInformation(
                    "Cache INVALIDATION: recent treatments pattern '{Pattern}' after updating treatment {TreatmentId}",
                    recentTreatmentsPattern,
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate treatment caches");
            }

            try
            {
                await _broadcastService.BroadcastStorageUpdateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = updatedTreatment }
                );
                _logger.LogDebug(
                    "Broadcasted storage update event for treatment {TreatmentId}",
                    updatedTreatment.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage update event for treatment {TreatmentId}",
                    updatedTreatment.Id
                );
            }
        }

        return updatedTreatment;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTreatmentAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Get the treatment before deleting for broadcasting
        var treatmentToDelete = await _postgreSqlService.GetTreatmentByIdAsync(
            id,
            cancellationToken
        );

        var deleted = await _postgreSqlService.DeleteTreatmentAsync(id, cancellationToken);

        if (deleted && treatmentToDelete != null)
        {
            // Invalidate all recent treatments caches since a treatment was deleted
            try
            {
                var recentTreatmentsPattern = CacheKeyBuilder.BuildRecentTreatmentsPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(
                    recentTreatmentsPattern,
                    cancellationToken
                );
                _logger.LogInformation(
                    "Cache INVALIDATION: recent treatments pattern '{Pattern}' after deleting treatment {TreatmentId}",
                    recentTreatmentsPattern,
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate treatment caches");
            }

            try
            {
                await _broadcastService.BroadcastStorageDeleteAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = treatmentToDelete }
                );
                _logger.LogDebug(
                    "Broadcasted storage delete event for treatment {TreatmentId}",
                    treatmentToDelete.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage delete event for treatment {TreatmentId}",
                    treatmentToDelete.Id
                );
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> DeleteTreatmentsAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        // For bulk operations, we'd need to get the treatments first if we want to broadcast individual delete events
        // For now, just delete without individual broadcasting (matches current controller behavior)
        var deletedCount = await _postgreSqlService.BulkDeleteTreatmentsAsync(
            find ?? "{}",
            cancellationToken
        );

        if (deletedCount > 0)
        {
            // Invalidate all recent treatments caches since treatments were deleted
            try
            {
                var recentTreatmentsPattern = CacheKeyBuilder.BuildRecentTreatmentsPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(
                    recentTreatmentsPattern,
                    cancellationToken
                );
                _logger.LogDebug(
                    "Invalidated recent treatments pattern '{Pattern}' after bulk deleting {Count} treatments",
                    recentTreatmentsPattern,
                    deletedCount
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate treatment caches");
            }
        }

        return deletedCount;
    }
}
