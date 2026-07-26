using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Resolves a tenant from its public share token, with a short-lived cache to keep
/// repeated reads off the database. Used by <see cref="Multitenancy.TenantResolutionMiddleware"/>
/// when a request arrives on the <c>{token}.share.{baseDomain}</c> host.
/// </summary>
/// <remarks>
/// Only successful lookups are cached (2-minute TTL). Misses are never cached: a brute-force
/// sweep uses distinct tokens, so caching misses would bloat memory without preventing the
/// per-token database hit — that is the job of rate limiting. Call <see cref="EvictByHash"/> when a
/// token is rotated or removed so the previous link stops resolving immediately.
///
/// The token is only ever stored and cached as its SHA-256 digest, so neither the database nor the
/// cache holds a value that can be replayed as a share link.
/// </remarks>
public sealed class ShareTokenCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly ILogger<ShareTokenCacheService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public ShareTokenCacheService(
        IMemoryCache cache,
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        ILogger<ShareTokenCacheService> logger)
    {
        _cache = cache;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the tenant owning the given share token, or <see langword="null"/> if no
    /// active or inactive tenant holds it.
    /// </summary>
    public async Task<TenantContext?> ResolveByTokenAsync(string token)
    {
        var tokenHash = CredentialHash.ShareToken(token);
        var cacheKey = CacheKey(tokenHash);

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.ShareToken == tokenHash)
            .Select(t => new { t.Id, t.Slug, t.DisplayName, t.IsActive, t.IsDemo })
            .FirstOrDefaultAsync();

        if (tenant == null)
            return null;

        // Stamp last-accessed on the database-hit path only: successful resolutions are
        // cached for CacheTtl, so the write is debounced to at most once per TTL per token.
        // Skipped for inactive tenants (the middleware rejects those requests, so nothing was
        // accessed), and non-fatal: the stamp is bookkeeping and must not fail the resolve.
        if (tenant.IsActive)
        {
            try
            {
                await dbContext.Tenants
                    .Where(t => t.Id == tenant.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ShareLastAccessedAt, DateTime.UtcNow));
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Failed to stamp share_last_accessed_at for tenant {TenantId}", tenant.Id);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to stamp share_last_accessed_at for tenant {TenantId}", tenant.Id);
            }
        }

        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);
        _cache.Set(cacheKey, tenantContext, CacheTtl);
        return tenantContext;
    }

    /// <summary>
    /// Evicts the cached resolution for a stored token digest. Call on rotate or disable, passing
    /// the value held in <c>tenants.share_token</c>.
    /// </summary>
    public void EvictByHash(string tokenHash) => _cache.Remove(CacheKey(tokenHash));

    private static string CacheKey(string tokenHash) => $"share-token:{tokenHash}";
}
