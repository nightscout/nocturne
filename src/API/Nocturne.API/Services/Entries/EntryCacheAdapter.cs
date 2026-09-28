using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// <see cref="IEntryCache"/> implementation that owns cache key construction, TTL policy,
/// and demo-mode isolation for <see cref="Entry"/> queries.
/// Only caches skip=0 queries with common counts to keep cache cardinality bounded.
/// Demo mode queries use a separate key suffix to prevent real data from leaking into demo views.
/// A history-clamped request (<see cref="ICategoryReadContext.IsHistoryClamped"/>) bypasses the
/// cache entirely: the keys are per tenant, so a hit could serve it rows an unclamped reader
/// loaded, and a write could hand its narrowed rows to an unclamped one.
/// </summary>
/// <seealso cref="IEntryCache"/>
/// <seealso cref="EntryService"/>
/// <seealso cref="IDemoModeService"/>
public class EntryCacheAdapter : IEntryCache
{
    private readonly ICacheService _cache;
    private readonly IDemoModeService _demoMode;
    private readonly ITenantAccessor _tenant;
    private readonly ICategoryReadContext _categoryReadContext;
    private readonly ILogger<EntryCacheAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntryCacheAdapter"/>.
    /// </summary>
    /// <param name="cache">The distributed cache service for get/set/remove operations.</param>
    /// <param name="demoMode">Demo mode service used to isolate demo and real data cache keys.</param>
    /// <param name="tenant">Provides the current tenant context for scoping all cache keys.</param>
    /// <param name="categoryReadContext">Says whether this request is history-clamped.</param>
    /// <param name="logger">The logger instance.</param>
    public EntryCacheAdapter(
        ICacheService cache,
        IDemoModeService demoMode,
        ITenantAccessor tenant,
        ICategoryReadContext categoryReadContext,
        ILogger<EntryCacheAdapter> logger)
    {
        _cache = cache;
        _demoMode = demoMode;
        _tenant = tenant;
        _categoryReadContext = categoryReadContext;
        _logger = logger;
    }

    private string TenantCacheId => _tenant.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public async Task<IReadOnlyList<Entry>?> GetOrComputeAsync(
        EntryQuery query,
        Func<Task<IReadOnlyList<Entry>>> compute,
        CancellationToken ct = default)
    {
        // Only cache skip=0 with common counts
        if (_categoryReadContext.IsHistoryClamped
            || query.Skip != 0
            || !EntryDomainLogic.IsCommonEntryCount(query.Count))
            return null;

        var demoSuffix = _demoMode.IsEnabled ? ":demo" : "";
        var cacheKey = CacheKeyBuilder.BuildRecentEntriesKey(TenantCacheId, query.Count, query.Type ?? query.Find)
            + demoSuffix;
        var ttl = TimeSpan.FromSeconds(CacheConstants.Defaults.RecentEntriesExpirationSeconds);

        _logger.LogDebug(
            "Cache lookup for recent entries (count: {Count}, type: {Type}, demoMode: {DemoMode})",
            query.Count,
            query.Type ?? query.Find ?? "all",
            _demoMode.IsEnabled);

        var result = await _cache.GetOrSetAsync(
            cacheKey,
            async () => (await compute()).ToList(),
            ttl,
            ct);

        return result;
    }

    public async Task<Entry?> GetOrComputeCurrentAsync(
        Func<Task<Entry?>> compute,
        CancellationToken ct = default)
    {
        if (_categoryReadContext.IsHistoryClamped)
            return await compute();

        var demoSuffix = _demoMode.IsEnabled ? ":demo" : "";
        var cacheKey = CacheKeyBuilder.BuildCurrentEntriesKey(TenantCacheId) + demoSuffix;
        var cacheTtl = TimeSpan.FromSeconds(CacheConstants.Defaults.CurrentEntryExpirationSeconds);

        var cached = await _cache.GetAsync<Entry>(cacheKey, ct);
        if (cached != null)
        {
            _logger.LogDebug("Cache HIT for current entry (demoMode: {DemoMode})", _demoMode.IsEnabled);
            return cached;
        }

        _logger.LogDebug("Cache MISS for current entry (demoMode: {DemoMode}), computing", _demoMode.IsEnabled);

        var entry = await compute();

        if (entry != null)
        {
            await _cache.SetAsync(cacheKey, entry, cacheTtl, ct);
            _logger.LogDebug("Cached current entry with {TTL}s TTL", cacheTtl.TotalSeconds);
        }

        return entry;
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        try
        {
            var currentKey = CacheKeyBuilder.BuildCurrentEntriesKey(TenantCacheId);
            var pattern = CacheKeyBuilder.BuildRecentEntriesPattern(TenantCacheId);

            await _cache.RemoveAsync(currentKey, ct);
            await _cache.RemoveByPatternAsync(pattern, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate entry caches");
        }
    }
}
