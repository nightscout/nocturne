using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Docs;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// The caches that hold a copy of a tenant row: the host resolution
/// (<see cref="TenantResolutionMiddleware"/>), the documentation paths' own
/// (<see cref="ScalarAuthProvider"/>) and the share-link resolution
/// (<see cref="ShareTokenCacheService"/>). A cache added for the tenant row is evicted here.
/// </summary>
public static class TenantCacheEviction
{
    /// <summary>
    /// Evicts every cache of the tenant. Call it after the write has committed, on every write to
    /// a tenant row that adds, removes, renames, activates or deactivates it or changes what the
    /// docs paths or a share link resolve to. Evicting a cache a write did not touch costs one read.
    /// </summary>
    /// <param name="shareTokenHash">The tenant's share-token hash before the write, or null when it has none.</param>
    public static void Evict(IMemoryCache cache, string slug, string? shareTokenHash = null)
    {
        TenantResolutionMiddleware.EvictTenant(cache, slug);
        ScalarAuthProvider.EvictTenant(cache, slug);
        if (shareTokenHash is not null)
            ShareTokenCacheService.EvictByHash(cache, shareTokenHash);
    }
}
