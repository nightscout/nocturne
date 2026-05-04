namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Defines the OAuth 2.0 scope taxonomy for Nocturne.
/// Three tiers: read, readwrite, and full access (*).
/// Delete is intentionally restricted to * only.
/// </summary>
/// <seealso cref="OAuthScope"/>
/// <seealso cref="ScopeTranslator"/>
/// <seealso cref="TenantPermissions"/>
public static class OAuthScopes
{
    // Grant types
    /// <summary>App grant: third-party application authorized by the user.</summary>
    public const string GrantTypeApp = "app";
    /// <summary>Follower grant: user-to-user data sharing (data owner grants access to follower).</summary>
    public const string GrantTypeFollower = "follower";
    /// <summary>Direct grant: programmatic API token without an OAuth client.</summary>
    public const string GrantTypeDirect = "direct";
    /// <summary>Guest grant: temporary read-only access via short-lived code, no account required.</summary>
    public const string GrantTypeGuest = "guest";

    // Core health data scopes

    /// <summary>Read-only access to glucose entries.</summary>
    public const string EntriesRead = "entries.read";
    /// <summary>Read and write access to glucose entries.</summary>
    public const string EntriesReadWrite = "entries.readwrite";
    /// <summary>Read-only access to treatments (boluses, carbs, temp basals, etc.).</summary>
    public const string TreatmentsRead = "treatments.read";
    /// <summary>Read and write access to treatments.</summary>
    public const string TreatmentsReadWrite = "treatments.readwrite";
    /// <summary>Read-only access to device status records.</summary>
    public const string DeviceStatusRead = "devicestatus.read";
    /// <summary>Read and write access to device status records.</summary>
    public const string DeviceStatusReadWrite = "devicestatus.readwrite";
    /// <summary>Read-only access to user profiles (therapy settings).</summary>
    public const string ProfileRead = "profile.read";
    /// <summary>Read and write access to user profiles.</summary>
    public const string ProfileReadWrite = "profile.readwrite";
    /// <summary>Read-only access to heart rate data.</summary>
    public const string HeartRateRead = "heartrate.read";
    /// <summary>Read and write access to heart rate data.</summary>
    public const string HeartRateReadWrite = "heartrate.readwrite";
    /// <summary>Read-only access to step count data.</summary>
    public const string StepCountRead = "stepcount.read";
    /// <summary>Read and write access to step count data.</summary>
    public const string StepCountReadWrite = "stepcount.readwrite";
    /// <summary>Read-only access to food records.</summary>
    public const string FoodRead = "food.read";
    /// <summary>Read and write access to food records.</summary>
    public const string FoodReadWrite = "food.readwrite";

    // Platform feature scopes

    /// <summary>Read-only access to notification settings and history.</summary>
    public const string NotificationsRead = "notifications.read";
    /// <summary>Read and write access to notification settings.</summary>
    public const string NotificationsReadWrite = "notifications.readwrite";
    /// <summary>Read-only access to generated reports.</summary>
    public const string ReportsRead = "reports.read";

    // Account-level scopes

    /// <summary>Read-only access to the user's identity information.</summary>
    public const string IdentityRead = "identity.read";
    /// <summary>Read and write access to sharing/follower configuration.</summary>
    public const string SharingReadWrite = "sharing.readwrite";

    // Full access (includes delete)

    /// <summary>Superuser scope granting all permissions including delete.</summary>
    public const string FullAccess = "*";

    // Convenience aliases

    /// <summary>Convenience alias that expands to read scopes for all core health data types.</summary>
    public const string HealthRead = "health.read";
    /// <summary>Convenience alias that expands to read-write scopes for all core health data types.</summary>
    public const string HealthReadWrite = "health.readwrite";

    /// <summary>
    /// All individual scopes that can be requested (excluding aliases and full access).
    /// </summary>
    public static readonly IReadOnlyList<string> AllScopes = new[]
    {
        EntriesRead,
        EntriesReadWrite,
        TreatmentsRead,
        TreatmentsReadWrite,
        DeviceStatusRead,
        DeviceStatusReadWrite,
        ProfileRead,
        ProfileReadWrite,
        NotificationsRead,
        NotificationsReadWrite,
        ReportsRead,
        IdentityRead,
        HeartRateRead,
        HeartRateReadWrite,
        StepCountRead,
        StepCountReadWrite,
        FoodRead,
        FoodReadWrite,
        SharingReadWrite,
    };

    /// <summary>
    /// Scopes that are valid to request (including aliases and full access).
    /// </summary>
    public static readonly IReadOnlySet<string> ValidRequestScopes = new HashSet<string>(AllScopes)
    {
        FullAccess,
        HealthRead,
        HealthReadWrite,
    };

    /// <summary>
    /// Expansion of the health.read convenience alias.
    /// </summary>
    public static readonly IReadOnlyList<string> HealthReadExpansion = new[]
    {
        EntriesRead,
        TreatmentsRead,
        DeviceStatusRead,
        ProfileRead,
        HeartRateRead,
        StepCountRead,
        FoodRead,
    };

    /// <summary>
    /// Expansion of the health.readwrite convenience alias.
    /// </summary>
    public static readonly IReadOnlyList<string> HealthReadWriteExpansion = new[]
    {
        EntriesReadWrite,
        TreatmentsReadWrite,
        DeviceStatusReadWrite,
        ProfileReadWrite,
        HeartRateReadWrite,
        StepCountReadWrite,
        FoodReadWrite,
    };

    /// <summary>
    /// Maps each readwrite scope to its implied read scope.
    /// readwrite implicitly includes read.
    /// </summary>
    private static readonly Dictionary<string, string> ReadWriteImpliesRead = new()
    {
        [EntriesReadWrite] = EntriesRead,
        [TreatmentsReadWrite] = TreatmentsRead,
        [DeviceStatusReadWrite] = DeviceStatusRead,
        [ProfileReadWrite] = ProfileRead,
        [NotificationsReadWrite] = NotificationsRead,
        [HeartRateReadWrite] = HeartRateRead,
        [StepCountReadWrite] = StepCountRead,
        [FoodReadWrite] = FoodRead,
    };

    /// <summary>
    /// Check whether a scope string is a valid Nocturne OAuth scope.
    /// </summary>
    public static bool IsValid(string scope)
    {
        return ValidRequestScopes.Contains(scope);
    }

    /// <summary>
    /// Expand aliases and normalize a set of requested scopes into concrete scopes.
    /// - Expands health.read into its component scopes
    /// - readwrite scopes implicitly include their read counterpart (no need to list both)
    /// - * (full access) expands to all scopes
    /// </summary>
    public static IReadOnlySet<string> Normalize(IEnumerable<string> requestedScopes)
    {
        var result = new HashSet<string>();

        foreach (var scope in requestedScopes)
        {
            if (scope == FullAccess)
            {
                // Full access includes everything
                result.UnionWith(AllScopes);
                result.Add(FullAccess);
                return result;
            }

            if (scope == HealthRead)
            {
                result.UnionWith(HealthReadExpansion);
                continue;
            }

            if (scope == HealthReadWrite)
            {
                result.UnionWith(HealthReadWriteExpansion);
                continue;
            }

            if (ValidRequestScopes.Contains(scope))
            {
                result.Add(scope);
            }
        }

        return result;
    }

    /// <summary>
    /// Check if a set of granted scopes satisfies a required scope.
    /// Handles readwrite implying read, and * implying everything.
    /// </summary>
    public static bool SatisfiesScope(IEnumerable<string> grantedScopes, string requiredScope)
    {
        var granted = grantedScopes as ISet<string> ?? new HashSet<string>(grantedScopes);

        // Full access satisfies everything
        if (granted.Contains(FullAccess))
            return true;

        // Exact match
        if (granted.Contains(requiredScope))
            return true;

        // If requiring a .read scope, check if the corresponding .readwrite is granted
        return ReadWriteImpliesRead.Any(kvp => kvp.Value == requiredScope && granted.Contains(kvp.Key));
    }
}
