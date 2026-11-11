using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Constants;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// <see cref="ITreatmentCache"/> implementation that owns cache key construction and TTL policy
/// for <see cref="Treatment"/> queries, over <see cref="ITenantCache"/>.
/// Only caches skip=0 queries with common counts (10, 50, 100) and no find filter to keep
/// cache cardinality bounded.
/// </summary>
/// <seealso cref="ITreatmentCache"/>
/// <seealso cref="TreatmentService"/>
internal class TreatmentCacheAdapter : ITreatmentCache
{
    /// <summary>The <see cref="ITenantCache"/> key family every treatment read is cached under.</summary>
    private const string CacheFamily = TenantCacheFamily.Treatments;

    private readonly ITenantCache _cache;

    public TreatmentCacheAdapter(ITenantCache cache)
    {
        _cache = cache;
    }

    public async Task<IReadOnlyList<Treatment>?> GetOrComputeAsync(
        TreatmentQuery query,
        Func<Task<IReadOnlyList<Treatment>>> compute,
        CancellationToken ct)
    {
        if (query.Skip != 0 || !IsCommonCount(query.Count) || query.Find is not null)
            return null;

        var key = $"{CacheFamily}:recent:{DetermineTimeRangeHours(query.Count)}h:count:{query.Count}";
        var ttl = TimeSpan.FromSeconds(CacheConstants.Defaults.RecentTreatmentsExpirationSeconds);

        return await _cache.GetOrSetAsync(
            key,
            async () => (await compute()).ToList(),
            ttl,
            ct);
    }

    private static bool IsCommonCount(int count) => count is 10 or 50 or 100;

    private static int DetermineTimeRangeHours(int count) => count switch
    {
        <= 10 => 12,
        <= 50 => 24,
        _ => 48,
    };
}
