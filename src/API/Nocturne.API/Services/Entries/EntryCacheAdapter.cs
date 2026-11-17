using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
using Nocturne.Infrastructure.Cache.Constants;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// <see cref="IEntryCache"/> implementation that owns cache key construction and TTL policy for
/// <see cref="Entry"/> queries, over <see cref="ITenantCache"/>.
/// Only caches skip=0 queries with common counts to keep cache cardinality bounded.
/// </summary>
/// <seealso cref="IEntryCache"/>
/// <seealso cref="EntryService"/>
internal class EntryCacheAdapter : IEntryCache
{
    /// <summary>The <see cref="ITenantCache"/> key family every entry read is cached under.</summary>
    private const string CacheFamily = TenantCacheFamily.Entries;

    private readonly ITenantCache _cache;

    public EntryCacheAdapter(ITenantCache cache)
    {
        _cache = cache;
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

    public Task<Entry?> GetOrComputeCurrentAsync(
        Func<Task<Entry?>> compute,
        CancellationToken ct = default) =>
        _cache.GetOrSetUnlessNullAsync(
            $"{CacheFamily}:current",
            compute,
            TimeSpan.FromSeconds(CacheConstants.Defaults.CurrentEntryExpirationSeconds),
            ct);
}
