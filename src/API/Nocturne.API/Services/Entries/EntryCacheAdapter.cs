using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.API.Services.Glucose;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// <see cref="IEntryCache"/> implementation that owns cache key construction and TTL policy for
/// <see cref="Entry"/> queries, over <see cref="ITenantCache"/>.
/// Only caches skip=0 queries with common counts to keep cache cardinality bounded.
/// </summary>
/// <seealso cref="IEntryCache"/>
/// <seealso cref="EntryService"/>
public class EntryCacheAdapter : IEntryCache
{
    /// <summary>The <see cref="ITenantCache"/> key family every entry read is cached under.</summary>
    public const string CacheFamily = TenantCacheFamily.Entries;

    private readonly ITenantCache _cache;
    private readonly ILogger<EntryCacheAdapter> _logger;

    public EntryCacheAdapter(ITenantCache cache, ILogger<EntryCacheAdapter> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Entry>?> GetOrComputeAsync(
        EntryQuery query,
        Func<Task<IReadOnlyList<Entry>>> compute,
        CancellationToken ct = default)
    {
        if (query.Skip != 0 || !EntryDomainLogic.IsCommonEntryCount(query.Count))
            return null;

        var type = query.Type ?? query.Find;
        var cacheKey = string.IsNullOrEmpty(type)
            ? $"{CacheFamily}:recent:{query.Count}"
            : $"{CacheFamily}:recent:{query.Count}:type:{type}";
        var ttl = TimeSpan.FromSeconds(CacheConstants.Defaults.RecentEntriesExpirationSeconds);

        return await _cache.GetOrSetAsync(
            cacheKey,
            async () => (await compute()).ToList(),
            ttl,
            ct);
    }

    public async Task<Entry?> GetOrComputeCurrentAsync(
        Func<Task<Entry?>> compute,
        CancellationToken ct = default)
    {
        const string cacheKey = $"{CacheFamily}:current";

        var cached = await _cache.GetAsync<Entry>(cacheKey, ct);
        if (cached != null)
            return cached;

        var entry = await compute();
        if (entry != null)
        {
            await _cache.SetAsync(
                cacheKey,
                entry,
                TimeSpan.FromSeconds(CacheConstants.Defaults.CurrentEntryExpirationSeconds),
                ct);
        }

        return entry;
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        try
        {
            await _cache.RemoveFamilyAsync(CacheFamily, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate entry caches");
        }
    }
}
