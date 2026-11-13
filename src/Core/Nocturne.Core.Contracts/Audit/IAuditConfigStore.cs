namespace Nocturne.Core.Contracts.Audit;

/// <summary>Writes the current tenant's audit configuration and reads the retention window it is checked against.</summary>
public interface IAuditConfigStore
{
    /// <summary>The current tenant's own soft-delete retention days, or null when it has no override row or none set.</summary>
    Task<int?> GetSoftDeleteRetentionDaysAsync(CancellationToken ct = default);

    /// <summary>Creates or replaces the current tenant's audit configuration and returns what is stored.</summary>
    Task<TenantAuditConfig> SaveAsync(TenantAuditConfig config, CancellationToken ct = default);
}
