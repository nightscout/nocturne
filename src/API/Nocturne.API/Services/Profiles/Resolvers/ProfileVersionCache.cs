using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Caches every stored version of a profile's schedule and picks the version active at the asked
/// time from the cached list, so the answer never depends on which time was looked up first.
/// A share or history-clamped request bypasses the cache: the key carries only the tenant, and RLS
/// hides these tables from a share, so a share must neither read the owner's versions nor leave an
/// empty list for the owner to read.
/// </summary>
internal sealed class ProfileVersionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly ITenantAccessor _tenantAccessor;
    private readonly ICategoryReadContext _categoryReadContext;
    private readonly IMemoryCache _cache;

    public ProfileVersionCache(
        ITenantAccessor tenantAccessor,
        ICategoryReadContext categoryReadContext,
        IMemoryCache cache)
    {
        _tenantAccessor = tenantAccessor;
        _categoryReadContext = categoryReadContext;
        _cache = cache;
    }

    /// <summary>
    /// The newest version at-or-before <paramref name="timestamp"/>, matching
    /// <see cref="IProfileScopedRepository{TRecord}.GetActiveAtAsync"/>.
    /// </summary>
    public async Task<TRecord?> GetActiveAtAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo,
        string profileName,
        DateTime timestamp,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        if (_categoryReadContext.IsShare || _categoryReadContext.IsHistoryClamped)
            return await repo.GetActiveAtAsync(profileName, timestamp, ct);

        var cacheKey = $"ProfileVersions:{typeof(TRecord).Name}:{_tenantAccessor.TenantId}:{profileName}";

        if (!_cache.TryGetValue(cacheKey, out TRecord[]? versions) || versions is null)
        {
            versions = (await repo.GetByProfileNameAsync(profileName, ct))
                .OrderByDescending(v => v.Timestamp)
                .ToArray();
            _cache.Set(cacheKey, versions, Ttl);
        }

        return versions.FirstOrDefault(v => v.Timestamp <= timestamp);
    }
}
