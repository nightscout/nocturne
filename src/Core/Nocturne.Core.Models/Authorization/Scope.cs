namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The scope vocabulary. Every authorization atom in Nocturne is declared here once, whether it is
/// reached through an OAuth grant or through a tenant membership.
///
/// Atoms fall into three tiers: read, readwrite, and full access (<c>*</c>). A readwrite scope
/// carries the whole write authority over its own data category — create, update and delete of a
/// single record alike. Withholding delete from a grant that may already rewrite every field of a
/// record buys nothing, and the V4 surface has always read the tier that way. Full access differs
/// by spanning every category and the tenant-administration atoms; the one data verb it alone
/// unlocks is the V1 query-driven bulk delete, which empties a collection in one request and so is
/// not something a per-category grant should reach.
///
/// Two sets carve the vocabulary up, and the distinction is load-bearing:
/// <see cref="ValidRequestScopes"/> is what an OAuth client may ask for at <c>/authorize</c>;
/// <see cref="MemberGrantableScopes"/> is the wider set a tenant role may confer. The
/// tenant-administration atoms sit only in the second, because there is no per-client scope ceiling
/// to bound a request for them and no consent screen that could meaningfully describe them.
/// </summary>
/// <seealso cref="RoleSeeds"/>
/// <seealso cref="ScopeTranslator"/>
/// <seealso cref="MemberScopeResolver"/>
public static class Scope
{
    // Patient record

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
    /// <summary>Read-only access to generated reports.</summary>
    public const string ReportsRead = "reports.read";

    // Therapy settings

    /// <summary>Read-only access to therapy settings (profiles).</summary>
    public const string TherapyRead = "therapy.read";
    /// <summary>Read and write access to therapy settings.</summary>
    public const string TherapyReadWrite = "therapy.readwrite";
    /// <summary>Read-only access to alert settings and history.</summary>
    public const string AlertsRead = "alerts.read";
    /// <summary>Read and write access to alert settings.</summary>
    public const string AlertsReadWrite = "alerts.readwrite";

    // Account

    /// <summary>Read-only access to identity information.</summary>
    public const string IdentityRead = "identity.read";

    // Sharing
    //
    // Three distinct atoms, deliberately not interchangeable. SharingReadWrite is the OAuth-facing
    // scope over a subject's own sharing configuration — a client asks for it at /authorize.
    // SharingManage and SharingGuest are tenant-administration atoms conferred by a role: one mints
    // public links to the tenant, the other mints short-lived guest links. A client cannot request
    // either, and holding SharingReadWrite confers neither.

    /// <summary>Read and write access to the subject's own sharing/follower configuration.</summary>
    public const string SharingReadWrite = "sharing.readwrite";
    /// <summary>Permission to manage tenant sharing and follower grants.</summary>
    public const string SharingManage = "sharing.manage";
    /// <summary>Permission to create temporary guest access links.</summary>
    public const string SharingGuest = "sharing.guest";

    // Tenant administration

    /// <summary>Permission to create, edit, and delete tenant roles.</summary>
    public const string RolesManage = "roles.manage";
    /// <summary>Permission to invite new members to the tenant.</summary>
    public const string MembersInvite = "members.invite";
    /// <summary>Permission to manage existing tenant members (change roles, remove).</summary>
    public const string MembersManage = "members.manage";
    /// <summary>Permission to modify tenant-level settings.</summary>
    public const string TenantSettings = "tenant.settings";

    // Audit

    /// <summary>Read-only access to the mutation audit log.</summary>
    public const string AuditRead = "audit.read";
    /// <summary>Permission to manage audit settings (retention, export).</summary>
    public const string AuditManage = "audit.manage";

    // Client devices
    //
    // Capability grants, not data access: they authorize the alert engine to drive the member's own
    // registered client devices (Prelude, the desktop Companion), whose rows are RLS-scoped to the
    // member's subject. They have no read/write tiers and do not imply one another.

    /// <summary>Allows the alert engine to push notifications to a registered client device.</summary>
    public const string DeviceNotify = "device.notify";
    /// <summary>Allows the alert engine to actuate hardware on a registered client device (torch, vibration, sound, full-screen).</summary>
    public const string DeviceActuate = "device.actuate";

    // Full access

    /// <summary>Superuser scope, satisfying every other scope across every data category.</summary>
    public const string FullAccess = "*";

    // Convenience aliases

    /// <summary>Convenience alias that expands to read scopes for all core health data types.</summary>
    public const string HealthRead = "health.read";
    /// <summary>Convenience alias that expands to read-write scopes for all core health data types.</summary>
    public const string HealthReadWrite = "health.readwrite";

    // Sets

    /// <summary>
    /// Every atom an OAuth client may hold on a grant, excluding aliases and full access.
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
    /// Scopes that are valid to request at <c>/authorize</c>, including aliases and full access.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidRequestScopes = new HashSet<string>(AllScopes)
    {
        FullAccess,
        HealthRead,
        HealthReadWrite,
    };

    /// <summary>
    /// Every atom a tenant role or a direct member permission may confer, excluding full access.
    /// Wider than <see cref="AllScopes"/> by the tenant-administration atoms.
    /// </summary>
    public static readonly IReadOnlySet<string> PermissionAtoms = new HashSet<string>(StringComparer.Ordinal)
    {
        GlucoseRead, GlucoseReadWrite,
        TreatmentsRead, TreatmentsReadWrite,
        DevicesRead, DevicesReadWrite,
        HeartRateRead, HeartRateReadWrite,
        StepCountRead, StepCountReadWrite,
        SleepRead, SleepReadWrite,
        FoodRead, FoodReadWrite,
        ReportsRead,
        TherapyRead, TherapyReadWrite,
        AlertsRead, AlertsReadWrite,
        IdentityRead,
        RolesManage,
        MembersInvite,
        MembersManage,
        TenantSettings,
        SharingManage,
        SharingGuest,
        AuditRead,
        AuditManage,
        DeviceNotify,
        DeviceActuate,
    };

    /// <summary>
    /// Every scope a tenant membership can resolve to: <see cref="ValidRequestScopes"/> plus every
    /// atom in <see cref="PermissionAtoms"/>. The two differ over the tenant-administration atoms
    /// (<c>members.manage</c>, <c>roles.manage</c>, <c>tenant.settings</c>, <c>audit.read</c>, …):
    /// a tenant role may confer them, but a client may not request them at <c>/authorize</c> and a
    /// user cannot consent to them, because there is no per-client scope ceiling to bound such a
    /// request.
    /// </summary>
    /// <seealso cref="NormalizeMemberPermissions"/>
    public static readonly IReadOnlySet<string> MemberGrantableScopes =
        new HashSet<string>(ValidRequestScopes.Concat(PermissionAtoms), StringComparer.Ordinal);

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
    /// Read scopes that may be granted to the Public subject for anonymous share-link access.
    /// These map directly to the data categories shown in the Sharing &amp; Privacy UI. The owner
    /// chooses which subset is visible to anyone holding the public link.
    /// </summary>
    public static readonly IReadOnlySet<string> PublicShareScopes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            GlucoseRead, TreatmentsRead, DevicesRead,
            HeartRateRead, StepCountRead, FoodRead, ReportsRead,
        };

    /// <summary>
    /// Scopes granted to the Public subject when a share link is first enabled. Defaults to glucose
    /// only; the owner opts into additional categories from <see cref="PublicShareScopes"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPublicShareScopes = [GlucoseRead];

    /// <summary>
    /// Member-personal capability scopes. These authorize the alert engine to drive the member's
    /// OWN registered client devices (rows are RLS-scoped to the member's subject), not access to
    /// the patient record, so <c>MemberScopeMiddleware</c> exempts them from the role-permission
    /// intersection for any member holding at least one permission. See the note on
    /// <see cref="RoleSeeds.Permissions"/> for why enforcement cannot rely on role rows.
    /// </summary>
    public static readonly IReadOnlySet<string> MemberPersonalScopes =
        new HashSet<string>(StringComparer.Ordinal) { DeviceNotify, DeviceActuate };

    /// <summary>
    /// Permissions for a demo tenant's shared visitor member: everything needed to explore and
    /// change the patient-facing surfaces, and nothing that manages who can get in.
    /// </summary>
    /// <remarks>
    /// Anyone can obtain a session for that member, so it must not hold
    /// <see cref="MembersManage"/>, <see cref="MembersInvite"/> or <see cref="RolesManage"/>.
    /// Member management is an escalation primitive — direct permissions and role permissions
    /// are unioned into the member's effective set, so the ability to edit either is the
    /// ability to grant oneself <see cref="FullAccess"/>. <see cref="SharingManage"/> and
    /// <see cref="AuditRead"/> are excluded for the same reason: one mints public links to the
    /// tenant, the other reads who did what.
    /// </remarks>
    public static readonly IReadOnlyList<string> DemoVisitorPermissions =
    [
        GlucoseReadWrite, TreatmentsReadWrite, DevicesReadWrite,
        HeartRateReadWrite, StepCountReadWrite, SleepReadWrite, FoodReadWrite,
        ReportsRead,
        TherapyReadWrite, AlertsReadWrite,
        IdentityRead,
        TenantSettings,
        DeviceNotify, DeviceActuate,
    ];

    /// <summary>Expansion of the <see cref="HealthRead"/> convenience alias.</summary>
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

    /// <summary>Expansion of the <see cref="HealthReadWrite"/> convenience alias.</summary>
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

    // Implication

    /// <summary>
    /// Maps each readwrite scope to the read scope it narrows to. Used to downgrade a readwrite
    /// grant when a narrower credential bounds it, which is a different question from whether a
    /// granted set satisfies a requirement — see <see cref="SatisfiedBy"/>.
    /// </summary>
    private static readonly Dictionary<string, string> ReadWriteImpliesRead = new(StringComparer.Ordinal)
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
    /// The read tier of the vocabulary: every atom that confers sight of a data category and no
    /// authority over it. The atoms <see cref="ReadWriteImpliesRead"/> narrows to, plus the four
    /// that exist only as read. Nothing else belongs, whatever it is spelled like: the
    /// tenant-administration atoms and the client-device capability atoms are not reads, and
    /// <see cref="FullAccess"/> spans every tier.
    /// </summary>
    /// <seealso cref="IsReadScope"/>
    public static readonly IReadOnlySet<string> ReadScopes =
        new HashSet<string>(ReadWriteImpliesRead.Values, StringComparer.Ordinal)
        {
            ReportsRead,
            IdentityRead,
            AuditRead,
            HealthRead,
        };

    /// <summary>
    /// For each atom, the other atoms that satisfy it without matching it. Every readwrite tier
    /// satisfies its read tier, and <see cref="AuditManage"/> satisfies <see cref="AuditRead"/> —
    /// a member who may change what is audited may read the log.
    ///
    /// Kept separate from <see cref="ReadWriteImpliesRead"/> because the two answer different
    /// questions: this one widens a check, that one narrows a grant. Narrowing
    /// <see cref="AuditManage"/> to <see cref="AuditRead"/> would silently strip a member's manage
    /// rights when an OAuth credential bounds their membership, so it is deliberately absent there.
    /// </summary>
    private static readonly Dictionary<string, string[]> SatisfiedBy = BuildSatisfiedBy();

    private static Dictionary<string, string[]> BuildSatisfiedBy()
    {
        var implications = new List<KeyValuePair<string, string>>(
            ReadWriteImpliesRead.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value)))
        {
            new(AuditManage, AuditRead),
        };

        return implications
            .GroupBy(kvp => kvp.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.Key).ToArray(), StringComparer.Ordinal);
    }

    // Queries

    /// <summary>
    /// Whether <paramref name="granted"/> on its own satisfies <paramref name="required"/>.
    /// <see cref="FullAccess"/> satisfies everything, an atom satisfies itself, and the
    /// implications in <see cref="SatisfiedBy"/> apply.
    /// </summary>
    /// <param name="granted">The atom that has been granted.</param>
    /// <param name="required">The atom that is required.</param>
    public static bool Satisfies(string granted, string required)
    {
        if (string.Equals(granted, FullAccess, StringComparison.Ordinal)) return true;
        if (string.Equals(granted, required, StringComparison.Ordinal)) return true;

        return SatisfiedBy.TryGetValue(required, out var satisfying)
               && Array.IndexOf(satisfying, granted) >= 0;
    }

    /// <summary>
    /// Whether any atom in <paramref name="granted"/> satisfies <paramref name="required"/>.
    /// This is the single authorization predicate: OAuth scope gates and tenant permission checks
    /// both resolve here, so a caller cannot be admitted by one and refused by the other.
    /// </summary>
    /// <param name="granted">The granted atoms.</param>
    /// <param name="required">The atom that is required.</param>
    public static bool Satisfies(IEnumerable<string> granted, string required)
    {
        var set = granted as ISet<string> ?? new HashSet<string>(granted, StringComparer.Ordinal);

        if (set.Contains(FullAccess)) return true;
        if (set.Contains(required)) return true;

        return SatisfiedBy.TryGetValue(required, out var satisfying)
               && satisfying.Any(set.Contains);
    }

    /// <summary>
    /// Returns the read scope a readwrite scope narrows to, or <see langword="null"/> when the
    /// scope has no read counterpart (a capability grant, an already-read scope, or full access).
    /// </summary>
    /// <param name="readWriteScope">The readwrite scope to narrow.</param>
    public static string? ImpliedReadScope(string readWriteScope)
    {
        return ReadWriteImpliesRead.TryGetValue(readWriteScope, out var readScope) ? readScope : null;
    }

    /// <summary>
    /// Look up the read scope a readwrite scope narrows to. Lets a caller reduce a readwrite grant
    /// to its read counterpart, which <see cref="Satisfies(IEnumerable{string}, string)"/> cannot
    /// express: that answers whether a granted set covers a requirement, not what the closest
    /// covered scope is.
    /// </summary>
    /// <param name="readWriteScope">The readwrite scope to narrow.</param>
    /// <param name="readScope">The implied read scope, when one exists.</param>
    /// <returns><c>true</c> when <paramref name="readWriteScope"/> has a read counterpart.</returns>
    public static bool TryGetImpliedReadScope(string readWriteScope, out string readScope)
    {
        return ReadWriteImpliesRead.TryGetValue(readWriteScope, out readScope!);
    }

    /// <summary>
    /// Whether <paramref name="scope"/> is in the read tier. Asks the vocabulary rather than the
    /// string's shape, so an atom that reads like a read but confers more cannot pass for one.
    /// </summary>
    /// <param name="scope">The scope to classify.</param>
    /// <seealso cref="ReadScopes"/>
    public static bool IsReadScope(string scope) => ReadScopes.Contains(scope);

    /// <summary>Whether a scope string is one an OAuth client may request.</summary>
    public static bool IsValid(string scope)
    {
        return ValidRequestScopes.Contains(scope);
    }

    // Normalization

    /// <summary>
    /// Expand aliases and normalize a set of requested scopes into concrete scopes.
    /// <list type="bullet">
    /// <item>expands <see cref="HealthRead"/> into its component scopes</item>
    /// <item>expands <see cref="FullAccess"/> to every scope in <see cref="AllScopes"/></item>
    /// </list>
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

    // Validation

    /// <summary>
    /// The scope list to store on a grant of <paramref name="grantType"/>: deduplicated, ordered,
    /// every scope in the vocabulary, and — for a guest grant — within
    /// <see cref="AllowedGuestScopes"/>.
    /// </summary>
    /// <param name="scopes">The requested scopes.</param>
    /// <param name="grantType">The grant's type, from <see cref="OAuthGrantTypes"/>.</param>
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

            if (grantType == OAuthGrantTypes.Guest && !AllowedGuestScopes.Contains(scope))
            {
                throw new ArgumentException(
                    $"Scope '{scope}' is not allowed for guest links. Only read scopes are permitted.");
            }
        }

        return requested;
    }

    /// <summary>
    /// Validates a set of permissions a caller is trying to grant (to a member, a role, or an
    /// invite) against the scopes the caller itself holds. Every requested atom must be a known
    /// permission and must be satisfied by <paramref name="granterScopes"/>, so a grant can never
    /// exceed the granter's own access. <see cref="FullAccess"/> is satisfied only by
    /// <see cref="FullAccess"/>, so it cannot be minted by a non-superuser.
    /// </summary>
    /// <param name="requested">The permissions being granted. <c>null</c> or empty is always allowed.</param>
    /// <param name="granterScopes">
    /// The granting caller's resolved scopes. This works on a scope set because
    /// <see cref="MemberGrantableScopes"/> is a superset of <see cref="PermissionAtoms"/>, so every
    /// permission atom survives scope resolution on an unscoped credential.
    /// </param>
    /// <returns>The first violation, or <c>null</c> when the whole set is grantable.</returns>
    public static GrantCeilingViolation? ValidateGrant(
        IEnumerable<string>? requested,
        IEnumerable<string> granterScopes)
    {
        if (requested is null)
            return null;

        var granter = granterScopes as IReadOnlyCollection<string> ?? granterScopes.ToList();

        foreach (var permission in requested)
        {
            if (permission != FullAccess && !PermissionAtoms.Contains(permission))
            {
                return new GrantCeilingViolation(
                    GrantCeilingViolation.UnknownPermission,
                    $"'{permission}' is not a known permission.");
            }

            if (!Satisfies(granter, permission))
            {
                return new GrantCeilingViolation(
                    GrantCeilingViolation.ExceedsGranter,
                    $"Cannot grant '{permission}' because the caller does not hold it.");
            }
        }

        return null;
    }
}

/// <summary>
/// Why a grant was refused. <see cref="Code"/> is stable for a caller to branch on and for the
/// frontend to localise; <see cref="Description"/> is diagnostic and names the offending permission.
/// </summary>
public record GrantCeilingViolation(string Code, string Description)
{
    /// <summary>The permission is not in the vocabulary — malformed input rather than a refusal.</summary>
    public const string UnknownPermission = "unknown_permission";

    /// <summary>The caller does not hold the permission it is trying to confer.</summary>
    public const string ExceedsGranter = "grant_exceeds_granter";
}
