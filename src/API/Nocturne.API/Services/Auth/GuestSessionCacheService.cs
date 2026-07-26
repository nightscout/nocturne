using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Caches validated guest sessions to keep repeated reads off the database. Used by
/// <see cref="Middleware.Handlers.GuestSessionHandler"/> on every request that carries a
/// guest session cookie.
/// </summary>
/// <remarks>
/// Entries have a fixed TTL of 30 seconds and are keyed by tenant as well as grant, so a
/// resolution warmed on one tenant is never served to a request resolved to another. Both
/// hits and misses are cached. Call <see cref="Evict"/> when a grant is revoked or dismissed
/// so the link stops resolving immediately instead of at the end of the TTL.
/// </remarks>
public sealed class GuestSessionCacheService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;

    public GuestSessionCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Returns the cached resolution for the grant on the given tenant. A <see langword="true"/>
    /// return with a <see langword="null"/> <paramref name="session"/> is a cached miss.
    /// </summary>
    public bool TryGet(Guid tenantId, Guid grantId, out GuestSessionInfo? session)
        => _cache.TryGetValue(CacheKey(tenantId, grantId), out session);

    /// <summary>Caches the resolution (or the absence of one) for the grant on the given tenant.</summary>
    public void Set(Guid tenantId, Guid grantId, GuestSessionInfo? session)
        => _cache.Set(CacheKey(tenantId, grantId), session, CacheTtl);

    /// <summary>Evicts the cached resolution. Call on revoke or dismiss.</summary>
    public void Evict(Guid tenantId, Guid grantId)
        => _cache.Remove(CacheKey(tenantId, grantId));

    private static string CacheKey(Guid tenantId, Guid grantId) => $"guest-session:{tenantId}:{grantId}";
}
