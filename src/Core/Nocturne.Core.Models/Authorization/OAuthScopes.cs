namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Defines the OAuth 2.0 scope taxonomy for Nocturne.
/// Three tiers: read, readwrite, and full access (*).
/// Delete is intentionally restricted to * only.
/// </summary>
public static class OAuthScopes
{
    // Grant types
    /// <summary>App grant: third-party application authorized by the user.</summary>
    public const string GrantTypeApp = "app";
    /// <summary>Follower grant: user-to-user data sharing (data owner grants access to follower).</summary>
    public const string GrantTypeFollower = "follower";

    // Core health data scopes
    public const string EntriesRead = "entries.read";
    public const string EntriesReadWrite = "entries.readwrite";
    public const string TreatmentsRead = "treatments.read";
    public const string TreatmentsReadWrite = "treatments.readwrite";
    public const string DeviceStatusRead = "devicestatus.read";
    public const string DeviceStatusReadWrite = "devicestatus.readwrite";
    public const string ProfileRead = "profile.read";
    public const string ProfileReadWrite = "profile.readwrite";

    // Platform feature scopes
    public const string NotificationsRead = "notifications.read";
    public const string NotificationsReadWrite = "notifications.readwrite";
    public const string ReportsRead = "reports.read";

    // Account-level scopes
    public const string IdentityRead = "identity.read";
    public const string SharingReadWrite = "sharing.readwrite";

    // Full access (includes delete)
    public const string FullAccess = "*";

    // Convenience alias
    public const string HealthRead = "health.read";

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
        SharingReadWrite,
    };

    /// <summary>
    /// Scopes that are valid to request (including aliases and full access).
    /// </summary>
    public static readonly IReadOnlySet<string> ValidRequestScopes = new HashSet<string>(AllScopes)
    {
        FullAccess,
        HealthRead,
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
