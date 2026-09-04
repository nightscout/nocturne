namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Single source of truth for the effective soft-delete retention window a tenant is
/// subject to. <see cref="SoftDeleteCleanupService"/> hard-deletes soft-deleted rows
/// once they pass this window; the audit-config validator
/// (<c>AuditController.UpdateAuditConfig</c>) resolves the same window so mutation-audit
/// retention can be kept at least as long. If audit rows aged out first, a user-delete's
/// attribution would vanish while the soft-deleted entity was still recoverable.
/// <para>
/// The consequence is evidentiary only. Dedup does not consult the audit row:
/// <c>SoftDeleteDedupExtensions</c> decides from the <c>deleted_by_user</c> flag carried on
/// the soft-deleted row itself, so a purged audit trail cannot let a connector resync
/// recreate a user-deleted record.
/// </para>
/// </summary>
public static class SoftDeleteRetentionPolicy
{
    /// <summary>Floor applied to every tenant regardless of the configured or default value.</summary>
    public const int MinRetentionDays = 7;

    /// <summary>Fallback when neither the tenant nor configuration specifies a value.</summary>
    public const int FallbackRetentionDays = 30;

    /// <summary>Configuration key for the instance-wide default.</summary>
    public const string ConfigKey = "DataRetention:SoftDeleteRetentionDays";

    /// <summary>
    /// Resolves the effective retention window in days. A tenant with no configured value
    /// (<c>null</c>, including the case where no retention row exists) falls back to the
    /// instance default; the result is never below <see cref="MinRetentionDays"/>. There is
    /// no "kept indefinitely" state — every soft-deleted row is hard-deleted after this window.
    /// </summary>
    public static int ResolveDays(int? tenantConfigured, IConfiguration configuration)
    {
        var days = tenantConfigured ?? configuration.GetValue(ConfigKey, FallbackRetentionDays);
        return Math.Max(days, MinRetentionDays);
    }
}
