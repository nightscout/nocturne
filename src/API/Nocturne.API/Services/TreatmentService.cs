using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Mappers;

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
    private readonly IStateSpanService _stateSpanService;
    private readonly ILogger<TreatmentService> _logger;
    private const string CollectionName = "treatments";
    private const string DefaultTenantId = "default"; // TODO: Replace with actual tenant context

    public TreatmentService(
        IPostgreSqlService postgreSqlService,
        ISignalRBroadcastService broadcastService,
        ICacheService cacheService,
        IOptions<CacheConfiguration> cacheConfig,
        IDemoModeService demoModeService,
        IStateSpanService stateSpanService,
        ILogger<TreatmentService> logger
    )
    {
        _postgreSqlService = postgreSqlService;
        _broadcastService = broadcastService;
        _cacheService = cacheService;
        _cacheConfig = cacheConfig.Value;
        _demoModeService = demoModeService;
        _stateSpanService = stateSpanService;
        _logger = logger;
    }

    // BuildDemoModeFilterQuery removed - relying on database isolation

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

        // Use find query directly (no application-level demo filter needed due to DB isolation)
        var findQuery = find;

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
                skip: 0, // We'll handle skip in the merge
                findQuery: findQuery,
                reverseResults: false,
                cancellationToken: cancellationToken
            );
            return await MergeWithTempBasalsAsync(treatments, findQuery, actualCount, actualSkip, cancellationToken);
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

            var cachedTreatments = await _cacheService.GetOrSetAsync(
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
                        skip: 0, // We'll handle skip in the merge
                        findQuery: findQuery,
                        reverseResults: false,
                        cancellationToken: cancellationToken
                    );
                    return treatments.ToList();
                },
                cacheTtl,
                cancellationToken
            );
            return await MergeWithTempBasalsAsync(cachedTreatments, findQuery, actualCount, actualSkip, cancellationToken);
        }

        // Non-cached path for non-standard queries
        var allTreatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
            count: actualCount,
            skip: 0, // We'll handle skip in the merge
            findQuery: findQuery,
            reverseResults: false,
            cancellationToken: cancellationToken
        );
        return await MergeWithTempBasalsAsync(allTreatments, findQuery, actualCount, actualSkip, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Use null find query (no application-level demo filter needed due to DB isolation)
        string? findQuery = null;

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

            var cachedTreatments = await _cacheService.GetOrSetAsync(
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
                        skip: 0, // We'll handle skip in the merge
                        findQuery: findQuery,
                        reverseResults: false,
                        cancellationToken: cancellationToken
                    );
                    return treatments.ToList();
                },
                cacheTtl,
                cancellationToken
            );
            return await MergeWithTempBasalsAsync(cachedTreatments, findQuery, count, skip, cancellationToken);
        }

        // Non-cached path for non-standard queries
        var allTreatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
            count: count,
            skip: 0, // We'll handle skip in the merge
            findQuery: findQuery,
            reverseResults: false,
            cancellationToken: cancellationToken
        );
        return await MergeWithTempBasalsAsync(allTreatments, findQuery, count, skip, cancellationToken);
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

    /// <summary>
    /// Parse time range from MongoDB-style find query
    /// </summary>
    private static (long? from, long? to) ParseTimeRangeFromFind(string? find)
    {
        if (string.IsNullOrEmpty(find))
            return (null, null);

        long? from = null;
        long? to = null;

        // Look for $gte (greater than or equal - "from")
        var gteMatch = System.Text.RegularExpressions.Regex.Match(
            find, @"\$gte[=\]]+(\d+)");
        if (gteMatch.Success && long.TryParse(gteMatch.Groups[1].Value, out var gteVal))
            from = gteVal;

        // Look for $lte (less than or equal - "to")
        var lteMatch = System.Text.RegularExpressions.Regex.Match(
            find, @"\$lte[=\]]+(\d+)");
        if (lteMatch.Success && long.TryParse(lteMatch.Groups[1].Value, out var lteVal))
            to = lteVal;

        return (from, to);
    }

    /// <summary>
    /// Merges regular treatments with temp basals from StateSpans
    /// </summary>
    private async Task<IEnumerable<Treatment>> MergeWithTempBasalsAsync(
        IEnumerable<Treatment> treatments,
        string? findQuery,
        int count,
        int skip,
        CancellationToken cancellationToken)
    {
        var (fromMills, toMills) = ParseTimeRangeFromFind(findQuery);
        var tempBasalTreatments = await _stateSpanService.GetTempBasalsAsTreatmentsAsync(
            from: fromMills,
            to: toMills,
            count: count,
            skip: 0, // We'll handle skip in the merge
            cancellationToken: cancellationToken);

        // Merge and sort
        var allTreatments = treatments
            .Concat(tempBasalTreatments)
            .OrderByDescending(t => t.Mills)
            .Skip(skip)
            .Take(count)
            .ToList();

        return allTreatments;
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
        var treatmentList = treatments.ToList();
        var regularTreatments = new List<Treatment>();
        var tempBasalTreatments = new List<Treatment>();

        // Separate temp basals from regular treatments
        foreach (var treatment in treatmentList)
        {
            if (TreatmentStateSpanMapper.IsTempBasalTreatment(treatment))
            {
                tempBasalTreatments.Add(treatment);
            }
            else
            {
                regularTreatments.Add(treatment);
            }
        }

        var results = new List<Treatment>();

        // Process temp basals through StateSpanService
        foreach (var tempBasal in tempBasalTreatments)
        {
            try
            {
                var stateSpan = await _stateSpanService.CreateTempBasalFromTreatmentAsync(
                    tempBasal, cancellationToken);

                // Convert back to Treatment for response
                var createdTreatment = TreatmentStateSpanMapper.ToTreatment(stateSpan);
                if (createdTreatment != null)
                {
                    results.Add(createdTreatment);

                    // Broadcast as treatment for v1-v3 socket compatibility
                    try
                    {
                        await _broadcastService.BroadcastStorageCreateAsync(
                            CollectionName,
                            new { colName = CollectionName, doc = createdTreatment });
                        _logger.LogDebug(
                            "Broadcasted storage create event for temp basal treatment {TreatmentId}",
                            createdTreatment.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to broadcast storage create event for temp basal {TreatmentId}",
                            createdTreatment.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create temp basal StateSpan for treatment {Id}", tempBasal.Id);
            }
        }

        // Process regular treatments through existing path
        if (regularTreatments.Count > 0)
        {
            var createdTreatments = await _postgreSqlService.CreateTreatmentsAsync(
                regularTreatments,
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

            results.AddRange(createdTreatments);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateTreatmentAsync(
        string id,
        Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        // Check if this is a temp basal in StateSpans
        var existingStateSpan = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);

        if (existingStateSpan != null && existingStateSpan.Category == StateSpanCategory.TempBasal)
        {
            // Update as StateSpan
            var updatedSpan = TreatmentStateSpanMapper.ToStateSpan(treatment);
            if (updatedSpan != null)
            {
                updatedSpan.Id = existingStateSpan.Id;
                updatedSpan.OriginalId = existingStateSpan.OriginalId ?? id;

                var result = await _stateSpanService.UpdateStateSpanAsync(id, updatedSpan, cancellationToken);
                if (result != null)
                {
                    var updatedTreatment = TreatmentStateSpanMapper.ToTreatment(result);
                    if (updatedTreatment != null)
                    {
                        try
                        {
                            await _broadcastService.BroadcastStorageUpdateAsync(
                                CollectionName,
                                new { colName = CollectionName, doc = updatedTreatment });
                            _logger.LogDebug(
                                "Broadcasted storage update event for temp basal treatment {TreatmentId}",
                                updatedTreatment.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Failed to broadcast storage update event for temp basal {TreatmentId}",
                                updatedTreatment.Id);
                        }
                    }
                    return updatedTreatment;
                }
            }
            return null;
        }

        // Fall back to regular treatment update
        var regularUpdatedTreatment = await _postgreSqlService.UpdateTreatmentAsync(
            id,
            treatment,
            cancellationToken
        );

        if (regularUpdatedTreatment != null)
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
                    new { colName = CollectionName, doc = regularUpdatedTreatment }
                );
                _logger.LogDebug(
                    "Broadcasted storage update event for treatment {TreatmentId}",
                    regularUpdatedTreatment.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage update event for treatment {TreatmentId}",
                    regularUpdatedTreatment.Id
                );
            }
        }

        return regularUpdatedTreatment;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTreatmentAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Check if this is a temp basal in StateSpans
        var existingStateSpan = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);

        if (existingStateSpan != null && existingStateSpan.Category == StateSpanCategory.TempBasal)
        {
            var deleted = await _stateSpanService.DeleteStateSpanAsync(id, cancellationToken);

            if (deleted)
            {
                var treatmentForBroadcast = TreatmentStateSpanMapper.ToTreatment(existingStateSpan);
                if (treatmentForBroadcast != null)
                {
                    try
                    {
                        await _broadcastService.BroadcastStorageDeleteAsync(
                            CollectionName,
                            new { colName = CollectionName, doc = treatmentForBroadcast });
                        _logger.LogDebug(
                            "Broadcasted storage delete event for temp basal treatment {TreatmentId}",
                            treatmentForBroadcast.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to broadcast storage delete event for temp basal {TreatmentId}",
                            treatmentForBroadcast.Id);
                    }
                }
            }

            return deleted;
        }

        // Fall back to regular treatment delete
        // Get the treatment before deleting for broadcasting
        var treatmentToDelete = await _postgreSqlService.GetTreatmentByIdAsync(
            id,
            cancellationToken
        );

        var regularDeleted = await _postgreSqlService.DeleteTreatmentAsync(id, cancellationToken);

        if (regularDeleted && treatmentToDelete != null)
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

        return regularDeleted;
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
