using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for entry operations with WebSocket broadcasting
/// </summary>
public class EntryService : IEntryService
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly ICacheService _cacheService;
    private readonly CacheConfiguration _cacheConfig;
    private readonly IDemoModeService _demoModeService;
    private readonly IEntryDecomposer _entryDecomposer;
    private readonly ILogger<EntryService> _logger;
    private const string CollectionName = "entries";
    private const string DefaultTenantId = "default"; // TODO: Replace with actual tenant context

    public EntryService(
        IPostgreSqlService postgreSqlService,
        ISignalRBroadcastService broadcastService,
        ICacheService cacheService,
        IOptions<CacheConfiguration> cacheConfig,
        IDemoModeService demoModeService,
        IEntryDecomposer entryDecomposer,
        ILogger<EntryService> logger
    )
    {
        _postgreSqlService = postgreSqlService;
        _broadcastService = broadcastService;
        _cacheService = cacheService;
        _cacheConfig = cacheConfig.Value;
        _demoModeService = demoModeService;
        _entryDecomposer = entryDecomposer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesAsync(
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

        // Cache recent entries for common queries (skip = 0 and common counts)
        // Include demo mode in cache key to avoid mixing demo/non-demo data
        if (actualSkip == 0 && IsCommonEntryCount(actualCount))
        {
            var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
            var cacheKey =
                CacheKeyBuilder.BuildRecentEntriesKey(DefaultTenantId, actualCount, find)
                + demoSuffix;
            var cacheTtl = TimeSpan.FromSeconds(
                CacheConstants.Defaults.RecentEntriesExpirationSeconds
            );

            return await _cacheService.GetOrSetAsync(
                cacheKey,
                async () =>
                {
                    _logger.LogDebug(
                        "Cache MISS for recent entries (count: {Count}, type: {Type}, demoMode: {DemoMode}), fetching from database with filter: {Filter}",
                        actualCount,
                        find ?? "all",
                        _demoModeService.IsEnabled,
                        findQuery
                    );
                    var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
                        type: "sgv", // Default to SGV entries
                        count: actualCount,
                        skip: actualSkip,
                        findQuery: findQuery,
                        cancellationToken: cancellationToken
                    );
                    return entries.ToList();
                },
                cacheTtl,
                cancellationToken
            );
        }

        // Non-cached path for non-standard queries
        var allEntries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
            type: "sgv", // Default to SGV entries
            count: actualCount,
            skip: actualSkip,
            findQuery: findQuery,
            cancellationToken: cancellationToken
        );
        return allEntries;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type,
        int count,
        int skip,
        CancellationToken cancellationToken
    )
    {
        // Build query with demo mode filter at database level
        var findQuery = BuildDemoModeFilterQuery(null);

        // Cache recent entries for common queries (skip = 0 and common counts)
        // Include demo mode in cache key to avoid mixing demo/non-demo data
        if (skip == 0 && IsCommonEntryCount(count))
        {
            var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
            var cacheKey =
                CacheKeyBuilder.BuildRecentEntriesKey(DefaultTenantId, count, type) + demoSuffix;
            var cacheTtl = TimeSpan.FromSeconds(
                CacheConstants.Defaults.RecentEntriesExpirationSeconds
            );

            return await _cacheService.GetOrSetAsync(
                cacheKey,
                async () =>
                {
                    _logger.LogDebug(
                        "Cache MISS for recent entries (count: {Count}, type: {Type}, demoMode: {DemoMode}), fetching from database with filter: {Filter}",
                        count,
                        type ?? "all",
                        _demoModeService.IsEnabled,
                        findQuery
                    );
                    var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
                        type,
                        count,
                        skip,
                        findQuery,
                        cancellationToken: cancellationToken
                    );
                    return entries.ToList();
                },
                cacheTtl,
                cancellationToken
            );
        }

        // Non-cached path for non-standard queries
        var allEntries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
            type,
            count,
            skip,
            findQuery,
            cancellationToken: cancellationToken
        );
        return allEntries;
    }

    /// <summary>
    /// Determines if the entry count is common enough to cache
    /// </summary>
    /// <param name="count">The count to check</param>
    /// <returns>True if the count is common (10, 50, 100), false otherwise</returns>
    private static bool IsCommonEntryCount(int count)
    {
        return count is 10 or 50 or 100;
    }

    /// <summary>
    /// Builds a find query that filters by demo mode.
    /// This ensures filtering happens at the database level for efficiency.
    /// </summary>
    /// <param name="existingQuery">Optional existing query to merge with</param>
    /// <returns>A JSON find query string with data_source filter</returns>
    private string BuildDemoModeFilterQuery(string? existingQuery = null)
    {
        // When demo mode is enabled, filter for data_source = "demo-service"
        // When demo mode is disabled, filter for data_source != "demo-service" (null or other sources)
        string demoFilter;
        if (_demoModeService.IsEnabled)
        {
            demoFilter = $"\"data_source\":\"{Core.Constants.DataSources.DemoService}\"";
        }
        else
        {
            // Use $ne operator to exclude demo service data
            demoFilter =
                $"\"data_source\":{{\"$ne\":\"{Core.Constants.DataSources.DemoService}\"}}";
        }

        if (string.IsNullOrWhiteSpace(existingQuery) || existingQuery == "{}")
        {
            return "{" + demoFilter + "}";
        }

        // Merge with existing query - insert demo filter into existing JSON object
        var trimmed = existingQuery.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (string.IsNullOrEmpty(inner))
            {
                return "{" + demoFilter + "}";
            }
            return "{" + demoFilter + "," + inner + "}";
        }

        // If query doesn't look like JSON, just return demo filter
        return "{" + demoFilter + "}";
    }

    /// <summary>
    /// Filters entries based on demo mode status (legacy client-side filtering).
    /// This is kept for backward compatibility but database-level filtering is preferred.
    /// </summary>
    private IEnumerable<Entry> FilterEntriesByDemoMode(IEnumerable<Entry> entries)
    {
        var entriesList = entries.ToList();
        var demoEntries = entriesList
            .Where(e => e.DataSource == Core.Constants.DataSources.DemoService)
            .ToList();
        var nonDemoEntries = entriesList
            .Where(e => e.DataSource != Core.Constants.DataSources.DemoService)
            .ToList();

        _logger.LogDebug(
            "FilterEntriesByDemoMode: DemoModeEnabled={DemoMode}, TotalEntries={Total}, DemoEntries={Demo}, NonDemoEntries={NonDemo}",
            _demoModeService.IsEnabled,
            entriesList.Count,
            demoEntries.Count,
            nonDemoEntries.Count
        );

        if (_demoModeService.IsEnabled)
        {
            // In demo mode, ONLY return demo entries - never fall back to real data
            if (demoEntries.Count > 0)
            {
                _logger.LogDebug("Demo mode ON: Returning {Count} demo entries", demoEntries.Count);
                return demoEntries;
            }
            else
            {
                // No demo entries available - return empty to avoid exposing real data
                _logger.LogWarning(
                    "Demo mode is enabled but no demo entries found. Returning empty results. "
                        + "Ensure the Demo Service is running and generating data."
                );
                return Enumerable.Empty<Entry>();
            }
        }
        else
        {
            // Not in demo mode, only show non-demo entries
            _logger.LogDebug(
                "Demo mode OFF: Returning {Count} non-demo entries",
                nonDemoEntries.Count
            );
            return nonDemoEntries;
        }
    }

    /// <inheritdoc />
    public async Task<Entry?> GetEntryByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        return await _postgreSqlService.GetEntryByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Entry?> CheckForDuplicateEntryAsync(
        string? device,
        string type,
        double? sgv,
        long mills,
        int windowMinutes = 5,
        CancellationToken cancellationToken = default
    )
    {
        return await _postgreSqlService.CheckForDuplicateEntryAsync(
            device,
            type,
            sgv,
            mills,
            windowMinutes,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default
    )
    {
        var createdEntries = await _postgreSqlService.CreateEntriesAsync(
            entries,
            cancellationToken
        );

        // Invalidate current entry cache since new entries were created
        try
        {
            await _cacheService.RemoveAsync("entries:current", cancellationToken);
            _logger.LogInformation(
                "Cache INVALIDATION: entries:current after creating {Count} entries",
                createdEntries.Count()
            );

            // Invalidate all recent entries caches using pattern matching
            var recentEntriesPattern = CacheKeyBuilder.BuildRecentEntriesPattern(DefaultTenantId);
            await _cacheService.RemoveByPatternAsync(recentEntriesPattern, cancellationToken);
            _logger.LogInformation(
                "Cache INVALIDATION: recent entries pattern '{Pattern}' after creating {Count} entries",
                recentEntriesPattern,
                createdEntries.Count()
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate entry caches");
        }

        // Broadcast create events for each entry (replaces legacy ctx.bus.emit('storage-socket-create'))
        foreach (var entry in createdEntries)
        {
            try
            {
                await _broadcastService.BroadcastStorageCreateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = entry }
                );
                _logger.LogDebug("Broadcasted storage create event for entry {EntryId}", entry.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage create event for entry {EntryId}",
                    entry.Id
                );
            }
        }

        // Broadcast data update for real-time glucose updates (replaces legacy ctx.bus.emit('data-update'))
        try
        {
            await _broadcastService.BroadcastDataUpdateAsync(createdEntries.ToArray());
            _logger.LogDebug("Broadcasted data update for {Count} entries", createdEntries.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to broadcast data update for {Count} entries",
                createdEntries.Count()
            );
        }

        // Decompose each created entry into v4 tables
        foreach (var entry in createdEntries)
        {
            try
            {
                await _entryDecomposer.DecomposeAsync(entry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to decompose entry {EntryId} into v4 tables",
                    entry.Id
                );
            }
        }

        return createdEntries;
    }

    /// <inheritdoc />
    public async Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default
    )
    {
        var updatedEntry = await _postgreSqlService.UpdateEntryAsync(id, entry, cancellationToken);

        if (updatedEntry != null)
        {
            // Invalidate current entry cache since an entry was updated
            try
            {
                await _cacheService.RemoveAsync("entries:current", cancellationToken);
                _logger.LogInformation(
                    "Cache INVALIDATION: entries:current after updating entry {EntryId}",
                    id
                );

                // Invalidate all recent entries caches using pattern matching
                var recentEntriesPattern = CacheKeyBuilder.BuildRecentEntriesPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(recentEntriesPattern, cancellationToken);
                _logger.LogInformation(
                    "Cache INVALIDATION: recent entries pattern '{Pattern}' after updating entry {EntryId}",
                    recentEntriesPattern,
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate entry caches");
            }

            try
            {
                await _broadcastService.BroadcastStorageUpdateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = updatedEntry }
                );
                _logger.LogDebug(
                    "Broadcasted storage update event for entry {EntryId}",
                    updatedEntry.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage update event for entry {EntryId}",
                    updatedEntry.Id
                );
            }

            // Re-decompose the updated entry to keep v4 tables in sync
            try
            {
                await _entryDecomposer.DecomposeAsync(updatedEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to re-decompose updated entry {EntryId} into v4 tables",
                    updatedEntry.Id
                );
            }
        }

        return updatedEntry;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteEntryAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Delete corresponding v4 records by LegacyId
        try
        {
            await _entryDecomposer.DeleteByLegacyIdAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete v4 records for legacy entry {EntryId}", id);
        }

        // Get the entry before deleting for broadcasting
        var entryToDelete = await _postgreSqlService.GetEntryByIdAsync(id, cancellationToken);

        var deleted = await _postgreSqlService.DeleteEntryAsync(id, cancellationToken);

        if (deleted)
        {
            // Invalidate current entry cache since an entry was deleted
            try
            {
                await _cacheService.RemoveAsync("entries:current", cancellationToken);
                _logger.LogInformation(
                    "Cache INVALIDATION: entries:current after deleting entry {EntryId}",
                    id
                );

                // Invalidate all recent entries caches using pattern matching
                var recentEntriesPattern = CacheKeyBuilder.BuildRecentEntriesPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(recentEntriesPattern, cancellationToken);
                _logger.LogInformation(
                    "Cache INVALIDATION: recent entries pattern '{Pattern}' after deleting entry {EntryId}",
                    recentEntriesPattern,
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate entry caches");
            }

            if (entryToDelete != null)
            {
                try
                {
                    await _broadcastService.BroadcastStorageDeleteAsync(
                        CollectionName,
                        new { colName = CollectionName, doc = entryToDelete }
                    );
                    _logger.LogDebug(
                        "Broadcasted storage delete event for entry {EntryId}",
                        entryToDelete.Id
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to broadcast storage delete event for entry {EntryId}",
                        entryToDelete.Id
                    );
                }
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> DeleteEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        // For bulk operations, we'd need to get the entries first if we want to broadcast individual delete events
        // For now, just delete without individual broadcasting (matches current controller behavior)
        var deletedCount = await _postgreSqlService.BulkDeleteEntriesAsync(
            find ?? "{}",
            cancellationToken
        );

        if (deletedCount > 0)
        {
            // Invalidate current entry cache since entries were deleted
            try
            {
                await _cacheService.RemoveAsync("entries:current", cancellationToken);
                _logger.LogDebug(
                    "Invalidated current entry cache after bulk deleting {Count} entries",
                    deletedCount
                );

                // Invalidate all recent entries caches using pattern matching
                var recentEntriesPattern = CacheKeyBuilder.BuildRecentEntriesPattern(
                    DefaultTenantId
                );
                await _cacheService.RemoveByPatternAsync(recentEntriesPattern, cancellationToken);
                _logger.LogDebug(
                    "Invalidated recent entries pattern '{Pattern}' after bulk deleting {Count} entries",
                    recentEntriesPattern,
                    deletedCount
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate entry caches");
            }
        }

        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default)
    {
        var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
        var cacheKey = "entries:current" + demoSuffix;
        var cacheTtl = TimeSpan.FromSeconds(CacheConstants.Defaults.CurrentEntryExpirationSeconds);

        var cachedEntry = await _cacheService.GetAsync<Entry>(cacheKey, cancellationToken);
        if (cachedEntry != null)
        {
            _logger.LogDebug(
                "Cache HIT for current entry (demoMode: {DemoMode})",
                _demoModeService.IsEnabled
            );
            return cachedEntry;
        }

        _logger.LogDebug(
            "Cache MISS for current entry (demoMode: {DemoMode}), fetching from database",
            _demoModeService.IsEnabled
        );

        // Use database-level filtering by demo mode
        var findQuery = BuildDemoModeFilterQuery(null);
        var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
            type: "sgv",
            count: 1,
            skip: 0,
            findQuery: findQuery,
            cancellationToken: cancellationToken
        );

        var entry = entries.FirstOrDefault();

        if (entry != null)
        {
            await _cacheService.SetAsync(cacheKey, entry, cacheTtl, cancellationToken);
            _logger.LogDebug("Cached current entry with {TTL}s TTL", cacheTtl.TotalSeconds);
        }

        return entry;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string find,
        int count,
        int skip,
        CancellationToken cancellationToken = default
    )
    {
        // Add demo mode filter to the existing query
        var findQuery = BuildDemoModeFilterQuery(find);
        var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
            null,
            count,
            skip,
            findQuery,
            null,
            false,
            cancellationToken
        );
        return entries;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string? type,
        int count,
        int skip,
        string? findQuery,
        string? dateString,
        bool reverseResults,
        CancellationToken cancellationToken = default
    )
    {
        // Add demo mode filter to the existing query
        var demoFilteredQuery = BuildDemoModeFilterQuery(findQuery);
        var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
            type,
            count,
            skip,
            demoFilteredQuery,
            dateString,
            reverseResults,
            cancellationToken
        );
        return entries;
    }
}
