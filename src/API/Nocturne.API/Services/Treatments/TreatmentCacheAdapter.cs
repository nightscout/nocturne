using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.API.Services.Platform;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// <see cref="ITreatmentCache"/> implementation that owns cache key construction, TTL policy,
/// and demo-mode isolation for <see cref="Treatment"/> queries.
/// Only caches skip=0 queries with common counts (10, 50, 100) and no find filter to keep
/// cache cardinality bounded. Invalidation clears all recent-treatment entries for the tenant.
/// A history-clamped request bypasses the cache, for the reason given on
/// <see cref="Nocturne.API.Services.Entries.EntryCacheAdapter"/>.
/// </summary>
/// <seealso cref="ITreatmentCache"/>
/// <seealso cref="TreatmentService"/>
/// <seealso cref="IDemoModeService"/>
public class TreatmentCacheAdapter : ITreatmentCache
{
    private readonly ICacheService _cache;
    private readonly IDemoModeService _demoMode;
    private readonly ITenantAccessor _tenant;
    private readonly ICategoryReadContext _categoryReadContext;
    private readonly ILogger<TreatmentCacheAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TreatmentCacheAdapter"/>.
    /// </summary>
    /// <param name="cache">The distributed cache service for get/set/remove operations.</param>
    /// <param name="demoMode">Demo mode service used to isolate demo and real data cache keys.</param>
    /// <param name="tenant">Provides the current tenant context for scoping all cache keys.</param>
    /// <param name="categoryReadContext">Says whether this request is history-clamped.</param>
    /// <param name="logger">The logger instance.</param>
    public TreatmentCacheAdapter(
        ICacheService cache,
        IDemoModeService demoMode,
        ITenantAccessor tenant,
        ICategoryReadContext categoryReadContext,
        ILogger<TreatmentCacheAdapter> logger)
    {
        _cache = cache;
        _demoMode = demoMode;
        _tenant = tenant;
        _categoryReadContext = categoryReadContext;
        _logger = logger;
    }

    private string TenantCacheId => _tenant.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public async Task<IReadOnlyList<Treatment>?> GetOrComputeAsync(
        TreatmentQuery query,
        Func<Task<IReadOnlyList<Treatment>>> compute,
        CancellationToken ct)
    {
        // Only cache skip=0 with common counts and no find filter
        if (_categoryReadContext.IsHistoryClamped
            || query.Skip != 0
            || !IsCommonCount(query.Count)
            || query.Find is not null)
            return null;

        var hours = DetermineTimeRangeHours(query.Count);
        var demoSuffix = _demoMode.IsEnabled ? ":demo" : "";
        var key = CacheKeyBuilder.BuildRecentTreatmentsKey(TenantCacheId, hours, query.Count)
            + demoSuffix;
        var ttl = TimeSpan.FromSeconds(CacheConstants.Defaults.RecentTreatmentsExpirationSeconds);

        var result = await _cache.GetOrSetAsync(
            key,
            async () => (await compute()).ToList(),
            ttl,
            ct);

        return result;
    }

    public async Task InvalidateAsync(CancellationToken ct)
    {
        try
        {
            var pattern = CacheKeyBuilder.BuildRecentTreatmentsPattern(TenantCacheId);
            await _cache.RemoveByPatternAsync(pattern, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate treatment caches");
        }
    }

    private static bool IsCommonCount(int count) => count is 10 or 50 or 100;

    private static int DetermineTimeRangeHours(int count) => count switch
    {
        <= 10 => 12,
        <= 50 => 24,
        _ => 48,
    };
}
