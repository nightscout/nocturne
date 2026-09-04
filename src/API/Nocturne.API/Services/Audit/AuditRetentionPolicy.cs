namespace Nocturne.API.Services.Audit;

/// <summary>
/// Single source of truth for the effective audit retention window a tenant is subject to.
/// <see cref="AuditRetentionService"/> purges against it; <c>AuditController.UpdateAuditConfig</c>
/// resolves the same window so its floor check sees the value that will actually be applied.
/// </summary>
/// <remarks>
/// There is no "kept indefinitely" state. A tenant that has configured nothing is purged at the
/// platform default, so a null on the config row is the default rather than infinity — an
/// instance may still disable a default by configuring it to zero or less, which leaves only
/// explicitly configured tenants purged.
/// </remarks>
public static class AuditRetentionPolicy
{
    /// <summary>Platform retention applied to tenants that have not set their own.</summary>
    public const int FallbackRetentionDays = 90;

    /// <summary>Configuration key for the instance-wide read-audit default.</summary>
    public const string ReadConfigKey = "Audit:DefaultReadAuditRetentionDays";

    /// <summary>Configuration key for the instance-wide mutation-audit default.</summary>
    public const string MutationConfigKey = "Audit:DefaultMutationRetentionDays";

    /// <summary>
    /// Resolves the effective read-audit window, or null when no purge applies.
    /// </summary>
    public static int? ResolveReadDays(int? tenantConfigured, IConfiguration configuration) =>
        tenantConfigured ?? ResolveDefault(ReadConfigKey, configuration);

    /// <summary>
    /// Resolves the effective mutation-audit window, or null when no purge applies.
    /// </summary>
    public static int? ResolveMutationDays(int? tenantConfigured, IConfiguration configuration) =>
        tenantConfigured ?? ResolveDefault(MutationConfigKey, configuration);

    /// <summary>
    /// Reads a platform retention default. An unset key falls back to
    /// <see cref="FallbackRetentionDays"/>; a configured value of zero or less disables the
    /// default.
    /// </summary>
    public static int? ResolveDefault(string key, IConfiguration configuration)
    {
        var days = configuration.GetValue<int?>(key) ?? FallbackRetentionDays;
        return days > 0 ? days : null;
    }
}
