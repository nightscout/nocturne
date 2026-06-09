using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Resolves a tenant from its public share token, with a short-lived cache to keep
/// repeated reads off the database. Used by <see cref="Multitenancy.TenantResolutionMiddleware"/>
/// when a request arrives on the <c>{token}.share.{baseDomain}</c> host.
/// </summary>
/// <remarks>
/// Only successful lookups are cached (2-minute TTL). Misses are never cached: a brute-force
/// sweep uses distinct tokens, so caching misses would bloat memory without preventing the
/// per-token database hit — that is the job of rate limiting. Call <see cref="Evict"/> when a
/// token is rotated or removed so the previous link stops resolving immediately.
/// </remarks>
public sealed class ShareTokenCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public ShareTokenCacheService(
        IMemoryCache cache,
        IDbContextFactory<NocturneDbContext> dbContextFactory)
    {
        _cache = cache;
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Resolves the tenant owning the given share token, or <see langword="null"/> if no
    /// active or inactive tenant holds it.
    /// </summary>
    public async Task<TenantContext?> ResolveByTokenAsync(string token)
    {
        var cacheKey = CacheKey(token);

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.ShareToken == token)
            .Select(t => new { t.Id, t.Slug, t.DisplayName, t.IsActive, t.IsDemo })
            .FirstOrDefaultAsync();

        if (tenant == null)
            return null;

        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);
        _cache.Set(cacheKey, tenantContext, CacheTtl);
        return tenantContext;
    }

    /// <summary>Evicts the cached resolution for a token. Call on rotate or disable.</summary>
    public void Evict(string token) => _cache.Remove(CacheKey(token));

    private static string CacheKey(string token) => $"share-token:{token}";
}
