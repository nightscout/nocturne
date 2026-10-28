using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Singleton cache for per-tenant audit configuration.
/// Uses IDbContextFactory to query the database on cache miss or TTL expiry.
/// </summary>
public sealed class TenantAuditConfigCache : ITenantAuditConfigCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TenantAuditConfig DefaultConfig = new(ReadAuditEnabled: false, ReadAuditRetentionDays: null, MutationAuditRetentionDays: null);

    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, (TenantAuditConfig Config, DateTime CachedAt)> _cache = new();

    /// <inheritdoc />
    public TenantAuditConfigCache(IDbContextFactory<NocturneDbContext> contextFactory, TimeProvider time)
    {
        _contextFactory = contextFactory;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<TenantAuditConfig> GetConfigAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(tenantId, out var entry) && now - entry.CachedAt < CacheTtl)
        {
            return entry.Config;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await context.TenantAuditConfig
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var config = entity is not null
            ? new TenantAuditConfig(entity.ReadAuditEnabled, entity.ReadAuditRetentionDays, entity.MutationAuditRetentionDays)
            : DefaultConfig;

        _cache[tenantId] = (config, now);
        return config;
    }

    /// <inheritdoc />
    public void Invalidate(Guid tenantId)
    {
        _cache.TryRemove(tenantId, out _);
    }
}
