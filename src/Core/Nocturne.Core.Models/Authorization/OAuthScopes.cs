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
    public const string GlucoseRead = "glucose.read";
    /// <summary>Read and write access to glucose entries.</summary>
    public const string GlucoseReadWrite = "glucose.readwrite";
    /// <summary>Read-only access to treatments (boluses, carbs, temp basals, etc.).</summary>
    public const string TreatmentsRead = "treatments.read";
    /// <summary>Read and write access to treatments.</summary>
    public const string TreatmentsReadWrite = "treatments.readwrite";
    /// <summary>Read-only access to device status records.</summary>
    public const string DevicesRead = "devices.read";
    /// <summary>Read and write access to device status records.</summary>
    public const string DevicesReadWrite = "devices.readwrite";
    /// <summary>Read-only access to user profiles (therapy settings).</summary>
    public const string TherapyRead = "therapy.read";
    /// <summary>Read and write access to user profiles.</summary>
    public const string TherapyReadWrite = "therapy.readwrite";
    /// <summary>Read-only access to heart rate data.</summary>
    public const string HeartRateRead = "heartrate.read";
    /// <summary>Read and write access to heart rate data.</summary>
    public const string HeartRateReadWrite = "heartrate.readwrite";
    /// <summary>Read-only access to step count data.</summary>
    public const string StepCountRead = "stepcount.read";
    /// <summary>Read and write access to step count data.</summary>
    public const string StepCountReadWrite = "stepcount.readwrite";
    /// <summary>Read-only access to sleep sessions.</summary>
    public const string SleepRead = "sleep.read";
    /// <summary>Read and write access to sleep sessions.</summary>
    public const string SleepReadWrite = "sleep.readwrite";
    /// <summary>Read-only access to food records.</summary>
    public const string FoodRead = "food.read";
    /// <summary>Read and write access to food records.</summary>
    public const string FoodReadWrite = "food.readwrite";

    // Platform feature scopes

    /// <summary>Read-only access to alert settings and history.</summary>
    public const string AlertsRead = "alerts.read";
    /// <summary>Read and write access to alert settings.</summary>
    public const string AlertsReadWrite = "alerts.readwrite";
    /// <summary>Read-only access to generated reports.</summary>
    public const string ReportsRead = "reports.read";

    // Account-level scopes

    /// <summary>Read-only access to the user's identity information.</summary>
    public const string IdentityRead = "identity.read";
    /// <summary>Read and write access to sharing/follower configuration.</summary>
    public const string SharingReadWrite = "sharing.readwrite";

    // Device actuation scopes
    //
    // These are capability grants, not data-access scopes: they authorize the alert engine to
    // drive a registered client device (Prelude, the desktop Companion). They have no read/write
    // tiers and do not imply one another — a device that should both notify and actuate hardware
    // is granted both.

    /// <summary>Allows the alert engine to push notifications to a registered client device.</summary>
    public const string DeviceNotify = "device.notify";
    /// <summary>Allows the alert engine to actuate hardware on a registered client device (torch, vibration, sound, full-screen).</summary>
    public const string DeviceActuate = "device.actuate";

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
        GlucoseRead,
        GlucoseReadWrite,
        TreatmentsRead,
        TreatmentsReadWrite,
        DevicesRead,
        DevicesReadWrite,
        TherapyRead,
        TherapyReadWrite,
        AlertsRead,
        AlertsReadWrite,
        ReportsRead,
        IdentityRead,
        HeartRateRead,
        HeartRateReadWrite,
        StepCountRead,
        StepCountReadWrite,
        SleepRead,
        SleepReadWrite,
        FoodRead,
        FoodReadWrite,
        SharingReadWrite,
        DeviceNotify,
        DeviceActuate,
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
    /// Every scope a tenant membership can grant: <see cref="ValidRequestScopes"/> plus every
    /// <see cref="TenantPermissions.All">tenant permission atom</see>. The two sets differ over the
    /// tenant-administration atoms (<c>members.manage</c>, <c>roles.manage</c>, <c>tenant.settings</c>,
    /// <c>audit.read</c>, …): a tenant role may grant them, but a client may not request them at
    /// <c>/authorize</c> and a user cannot consent to them, because there is no per-client scope
    /// ceiling to bound such a request. Derived from <see cref="TenantPermissions.All"/> so a new
    /// permission atom is grantable without a second edit here.
    /// </summary>
    /// <seealso cref="NormalizeMemberPermissions"/>
    public static readonly IReadOnlySet<string> MemberGrantableScopes =
        new HashSet<string>(ValidRequestScopes.Concat(TenantPermissions.All));

    /// <summary>
    /// The scopes a guest grant may hold. A guest link is a short-lived share of one person's data
    /// with no account behind it, so it is capped at read and cannot be widened past this set by
    /// anyone — including the data owner whose subject id the grant records.
    /// </summary>
    /// <seealso cref="ValidateGrantScopes"/>
    public static readonly IReadOnlySet<string> AllowedGuestScopes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            GlucoseRead, TreatmentsRead, DevicesRead, TherapyRead, HeartRateRead, StepCountRead,
            SleepRead, AlertsRead, ReportsRead, IdentityRead, HealthRead,
        };

    /// <summary>
    /// Expansion of the health.read convenience alias.
    /// </summary>
    public static readonly IReadOnlyList<string> HealthReadExpansion = new[]
    {
        GlucoseRead,
        TreatmentsRead,
        DevicesRead,
        TherapyRead,
        HeartRateRead,
        StepCountRead,
        SleepRead,
        FoodRead,
    };

    /// <summary>
    /// Expansion of the health.readwrite convenience alias.
    /// </summary>
    public static readonly IReadOnlyList<string> HealthReadWriteExpansion = new[]
    {
        GlucoseReadWrite,
        TreatmentsReadWrite,
        DevicesReadWrite,
        TherapyReadWrite,
        HeartRateReadWrite,
        StepCountReadWrite,
        SleepReadWrite,
        FoodReadWrite,
    };

    /// <summary>
    /// Maps each readwrite scope to its implied read scope.
    /// readwrite implicitly includes read.
    /// </summary>
    private static readonly Dictionary<string, string> ReadWriteImpliesRead = new()
    {
        [GlucoseReadWrite] = GlucoseRead,
        [TreatmentsReadWrite] = TreatmentsRead,
        [DevicesReadWrite] = DevicesRead,
        [TherapyReadWrite] = TherapyRead,
        [AlertsReadWrite] = AlertsRead,
        [HeartRateReadWrite] = HeartRateRead,
        [StepCountReadWrite] = StepCountRead,
        [SleepReadWrite] = SleepRead,
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
    /// Look up the read scope a readwrite scope implies. Lets a caller narrow a readwrite grant to
    /// its read counterpart, which <see cref="SatisfiesScope"/> cannot express: it answers whether
    /// a granted set covers a required scope, not what the closest covered scope is.
    /// </summary>
    /// <param name="readWriteScope">The readwrite scope to narrow.</param>
    /// <param name="readScope">The implied read scope, when one exists.</param>
    /// <returns><c>true</c> when <paramref name="readWriteScope"/> has a read counterpart.</returns>
    public static bool TryGetImpliedReadScope(string readWriteScope, out string readScope)
    {
        return ReadWriteImpliesRead.TryGetValue(readWriteScope, out readScope!);
    }

    /// <summary>
    /// The scope list to store on a grant of <paramref name="grantType"/>: deduplicated, ordered, every
    /// scope in the vocabulary, and — for a guest grant — within <see cref="AllowedGuestScopes"/>.
    /// </summary>
    /// <param name="scopes">The requested scopes.</param>
    /// <param name="grantType">The grant's type, from the <c>GrantType*</c> constants.</param>
    /// <returns>The scopes to store.</returns>
    /// <exception cref="ArgumentException">
    /// A scope is not a recognised scope, or is wider than the grant type may hold. Callers surface
    /// this as <c>invalid_scope</c>.
    /// </exception>
    /// <remarks>
    /// The two paths that can set a guest grant's scopes go through here: creating a guest link and
    /// updating a grant. <c>OAuthGrantService.CreateOrUpdateGrantAsync</c> also assigns scopes but
    /// matches on a client id, which a guest grant does not have, so it cannot reach one. The
    /// authorization-code and device-code flows validate before a grant is created.
    /// </remarks>
    public static List<string> ValidateGrantScopes(IEnumerable<string> scopes, string grantType)
    {
        var requested = scopes.Distinct().OrderBy(s => s).ToList();

        foreach (var scope in requested)
        {
            if (!IsValid(scope))
            {
                throw new ArgumentException($"Scope '{scope}' is not a recognised scope.");
            }

            if (grantType == GrantTypeGuest && !AllowedGuestScopes.Contains(scope))
            {
                throw new ArgumentException(
                    $"Scope '{scope}' is not allowed for guest links. Only read scopes are permitted.");
            }
        }

        return requested;
    }

    /// <summary>
    /// Expand aliases and normalize a set of requested scopes into concrete scopes.
    /// - Expands health.read into its component scopes
    /// - readwrite scopes implicitly include their read counterpart (no need to list both)
    /// - * (full access) expands to all scopes
    /// Anything outside <see cref="ValidRequestScopes"/> is dropped, so this is safe to call on
    /// caller-supplied input.
    /// </summary>
    public static IReadOnlySet<string> Normalize(IEnumerable<string> requestedScopes)
    {
        return Normalize(requestedScopes, ValidRequestScopes);
    }

    /// <summary>
    /// Expand and normalize a tenant member's effective permissions into granted scopes.
    /// Behaves like <see cref="Normalize"/> but recognizes <see cref="MemberGrantableScopes"/>, so
    /// the tenant-administration atoms survive instead of being dropped. Call this only for
    /// permissions that came from a tenant membership (role rows and direct permissions), never
    /// for scopes supplied by a client.
    /// </summary>
    /// <seealso cref="MemberScopeResolver"/>
    public static IReadOnlySet<string> NormalizeMemberPermissions(IEnumerable<string> permissions)
    {
        return Normalize(permissions, MemberGrantableScopes);
    }

    private static IReadOnlySet<string> Normalize(
        IEnumerable<string> requestedScopes, IReadOnlySet<string> recognizedScopes)
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

            if (recognizedScopes.Contains(scope))
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
