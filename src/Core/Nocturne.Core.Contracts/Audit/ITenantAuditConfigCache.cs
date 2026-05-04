namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// Cached per-tenant audit configuration. Defaults to disabled with no retention limits.
/// </summary>
public record TenantAuditConfig(bool ReadAuditEnabled, int? ReadAuditRetentionDays, int? MutationAuditRetentionDays);

/// <summary>
/// Caches per-tenant audit configuration with TTL eviction.
/// Singleton — uses IDbContextFactory for DB access.
/// </summary>
public interface ITenantAuditConfigCache
{
    Task<TenantAuditConfig> GetConfigAsync(Guid tenantId, CancellationToken ct = default);
    void Invalidate(Guid tenantId);
}
