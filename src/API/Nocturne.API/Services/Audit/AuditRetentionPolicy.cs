namespace Nocturne.API.Services.Audit;

/// <summary>
/// Single source of truth for the effective audit retention window a tenant is subject to.
/// <see cref="AuditRetentionService"/> purges against it; <c>AuditController.UpdateAuditConfig</c>
/// resolves the same window so its floor check sees the value that will actually be applied.
/// </summary>
/// <remarks>
/// <para>
/// A tenant that has configured nothing is purged at the platform default, so a null on the
/// config row is the default rather than infinity.
/// </para>
/// <para>
/// A value of zero or less means opposite things on the two inputs, deliberately. On a
/// <em>tenant</em> value it is nonsense — a window that puts the cutoff at or after now — so it
/// is floored to <see cref="MinRetentionDays"/>. On a <em>platform configuration key</em> it is
/// the operator disabling the default outright, which yields null and leaves only explicitly
/// configured tenants purged. A tenant cannot switch its own purge off; an operator can.
/// </para>
/// </remarks>
public static class AuditRetentionPolicy
{
    /// <summary>Platform retention applied to tenants that have not set their own.</summary>
    public const int FallbackRetentionDays = 90;

    /// <summary>
    /// Floor applied to any configured window. A window of zero or less puts the purge cutoff at
    /// or after now, which would delete records written moments earlier.
    /// </summary>
    public const int MinRetentionDays = 1;

    /// <summary>Configuration key for the instance-wide read-audit default.</summary>
    public const string ReadConfigKey = "Audit:DefaultReadAuditRetentionDays";

    /// <summary>Configuration key for the instance-wide mutation-audit default.</summary>
    public const string MutationConfigKey = "Audit:DefaultMutationRetentionDays";

    /// <summary>
    /// Resolves the effective read-audit window, or null when no purge applies.
    /// </summary>
    public static int? ResolveReadDays(int? tenantConfigured, IConfiguration configuration) =>
        Floor(tenantConfigured) ?? ResolveDefault(ReadConfigKey, configuration);

    /// <summary>
    /// Resolves the effective mutation-audit window, or null when no purge applies.
    /// </summary>
    public static int? ResolveMutationDays(int? tenantConfigured, IConfiguration configuration) =>
        Floor(tenantConfigured) ?? ResolveDefault(MutationConfigKey, configuration);

    /// <summary>
    /// Reads a platform retention default. An unset key falls back to
    /// <see cref="FallbackRetentionDays"/>; a configured value of zero or less disables the
    /// default, leaving only explicitly configured tenants purged.
    /// </summary>
    public static int? ResolveDefault(string key, IConfiguration configuration)
    {
        var days = configuration.GetValue<int?>(key) ?? FallbackRetentionDays;
        return days > 0 ? days : null;
    }

    /// <summary>
    /// Applies <see cref="MinRetentionDays"/> to a tenant-supplied window. A stored row predating
    /// the DTO bound, or written by any path that bypasses it, is clamped here rather than
    /// trusted.
    /// </summary>
    private static int? Floor(int? tenantConfigured) =>
        tenantConfigured is { } days ? Math.Max(days, MinRetentionDays) : null;
}
