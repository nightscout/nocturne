namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Permission atoms for the tenant RBAC system.
/// Uses the resource.action format compatible with <see cref="OAuthScopes"/>.
/// </summary>
/// <seealso cref="TenantPermission"/>
/// <seealso cref="OAuthScopes"/>
/// <seealso cref="Role"/>
/// <seealso cref="ScopeTranslator"/>
public static class TenantPermissions
{
    // Patient Record

    /// <summary>Read-only access to glucose entries within the tenant.</summary>
    public const string GlucoseRead = "glucose.read";
    /// <summary>Read and write access to glucose entries within the tenant.</summary>
    public const string GlucoseReadWrite = "glucose.readwrite";
    /// <summary>Read-only access to treatments within the tenant.</summary>
    public const string TreatmentsRead = "treatments.read";
    /// <summary>Read and write access to treatments within the tenant.</summary>
    public const string TreatmentsReadWrite = "treatments.readwrite";
    /// <summary>Read-only access to device records within the tenant.</summary>
    public const string DevicesRead = "devices.read";
    /// <summary>Read and write access to device records within the tenant.</summary>
    public const string DevicesReadWrite = "devices.readwrite";
    /// <summary>Read-only access to heart rate data within the tenant.</summary>
    public const string HeartRateRead = "heartrate.read";
    /// <summary>Read and write access to heart rate data within the tenant.</summary>
    public const string HeartRateReadWrite = "heartrate.readwrite";
    /// <summary>Read-only access to step count data within the tenant.</summary>
    public const string StepCountRead = "stepcount.read";
    /// <summary>Read and write access to step count data within the tenant.</summary>
    public const string StepCountReadWrite = "stepcount.readwrite";
    /// <summary>Read-only access to sleep sessions within the tenant.</summary>
    public const string SleepRead = "sleep.read";
    /// <summary>Read and write access to sleep sessions within the tenant.</summary>
    public const string SleepReadWrite = "sleep.readwrite";
    /// <summary>Read-only access to food records within the tenant.</summary>
    public const string FoodRead = "food.read";
    /// <summary>Read and write access to food records within the tenant.</summary>
    public const string FoodReadWrite = "food.readwrite";
    /// <summary>Read-only access to generated reports within the tenant.</summary>
    public const string ReportsRead = "reports.read";

    // Therapy Settings

    /// <summary>Read-only access to therapy settings within the tenant.</summary>
    public const string TherapyRead = "therapy.read";
    /// <summary>Read and write access to therapy settings within the tenant.</summary>
    public const string TherapyReadWrite = "therapy.readwrite";
    /// <summary>Read-only access to alert settings within the tenant.</summary>
    public const string AlertsRead = "alerts.read";
    /// <summary>Read and write access to alert settings within the tenant.</summary>
    public const string AlertsReadWrite = "alerts.readwrite";

    // Account

    /// <summary>Read-only access to identity information within the tenant.</summary>
    public const string IdentityRead = "identity.read";

    // Administration

    /// <summary>Permission to create, edit, and delete tenant roles.</summary>
    public const string RolesManage = "roles.manage";
    /// <summary>Permission to invite new members to the tenant.</summary>
    public const string MembersInvite = "members.invite";
    /// <summary>Permission to manage existing tenant members (change roles, remove).</summary>
    public const string MembersManage = "members.manage";
    /// <summary>Permission to modify tenant-level settings.</summary>
    public const string TenantSettings = "tenant.settings";
    /// <summary>Permission to manage sharing and follower grants.</summary>
    public const string SharingManage = "sharing.manage";
    /// <summary>Permission to create temporary guest access links.</summary>
    public const string SharingGuest = "sharing.guest";

    // Audit

    /// <summary>Read-only access to the mutation audit log.</summary>
    public const string AuditRead = "audit.read";
    /// <summary>Permission to manage audit settings (retention, export).</summary>
    public const string AuditManage = "audit.manage";

    // Client devices
    //
    // Capability grants for the member's own registered client devices (Prelude, the desktop
    // Companion) — the alert engine drives the member's device, not the patient's hardware.
    // Mirror OAuthScopes.DeviceNotify / OAuthScopes.DeviceActuate.

    /// <summary>Allows the alert engine to push notifications to the member's registered client devices.</summary>
    public const string DeviceNotify = "device.notify";
    /// <summary>Allows the alert engine to actuate hardware (sound, torch, vibration) on the member's registered client devices.</summary>
    public const string DeviceActuate = "device.actuate";

    /// <summary>
    /// Member-personal capability scopes. These authorize the alert engine to drive the member's
    /// OWN registered client devices (rows are RLS-scoped to the member's subject), not access to
    /// the patient record, so <c>MemberScopeMiddleware</c> exempts them from the role-permission
    /// intersection for any member holding at least one permission. See the note on
    /// <see cref="SeedRolePermissions"/> for why enforcement cannot rely on role rows.
    /// </summary>
    public static readonly IReadOnlySet<string> MemberPersonalScopes = new HashSet<string>
    {
        DeviceNotify,
        DeviceActuate,
    };

    // Superuser

    /// <summary>Superuser permission that satisfies all other permissions.</summary>
    public const string Superuser = "*";

    /// <summary>
    /// All valid permission atoms (excluding superuser).
    /// </summary>
    public static readonly HashSet<string> All =
    [
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
    ];

    /// <summary>
    /// Read scopes that may be granted to the Public subject for anonymous share-link access.
    /// These map directly to the data categories shown in the Sharing &amp; Privacy UI. The owner
    /// chooses which subset is visible to anyone holding the public link.
    /// </summary>
    public static readonly HashSet<string> PublicShareScopes =
    [
        GlucoseRead, TreatmentsRead, DevicesRead,
        HeartRateRead, StepCountRead, FoodRead, ReportsRead,
    ];

    /// <summary>
    /// Scopes granted to the Public subject when a share link is first enabled. Defaults to glucose
    /// only; the owner opts into additional categories from <see cref="PublicShareScopes"/>.
    /// </summary>
    public static readonly List<string> DefaultPublicShareScopes = [GlucoseRead];

    /// <summary>
    /// Permissions for a demo tenant's shared visitor member: everything needed to explore and
    /// change the patient-facing surfaces, and nothing that manages who can get in.
    /// </summary>
    /// <remarks>
    /// Anyone can obtain a session for that member, so it must not hold
    /// <see cref="MembersManage"/>, <see cref="MembersInvite"/> or <see cref="RolesManage"/>.
    /// Member management is an escalation primitive — direct permissions and role permissions
    /// are unioned into the member's effective set, so the ability to edit either is the
    /// ability to grant oneself <see cref="Superuser"/>. <see cref="SharingManage"/> and
    /// <see cref="AuditRead"/> are excluded for the same reason: one mints public links to the
    /// tenant, the other reads who did what.
    /// </remarks>
    public static readonly List<string> DemoVisitorPermissions =
    [
        GlucoseReadWrite, TreatmentsReadWrite, DevicesReadWrite,
        HeartRateReadWrite, StepCountReadWrite, SleepReadWrite, FoodReadWrite,
        ReportsRead,
        TherapyReadWrite, AlertsReadWrite,
        IdentityRead,
        TenantSettings,
        DeviceNotify, DeviceActuate,
    ];

    /// <summary>
    /// Seed role slugs.
    /// </summary>
    public static class SeedRoles
    {
        public const string Owner = "owner";
        public const string Admin = "admin";
        public const string Caretaker = "caretaker";
        public const string Viewer = "viewer";
        public const string Clinician = "clinician";
        public const string Denied = "denied";
    }

    /// <summary>
    /// Default permissions for each seed role. Every authenticated human role lists the
    /// <see cref="DeviceNotify"/>/<see cref="DeviceActuate"/> capability grants — they control the
    /// member's own registered client devices, not the patient record — so the role editor shows
    /// them as part of the role's surface for new tenants. Enforcement does NOT depend on these
    /// atoms: seed roles are persisted per-tenant rows and <c>SeedRolesForTenantAsync</c> skips
    /// slugs that already exist, so tenants seeded before an atom was added never receive it.
    /// <c>MemberScopeMiddleware</c> therefore grants <see cref="MemberPersonalScopes"/> from the
    /// auth token alone (for members holding at least one permission).
    /// </summary>
    public static readonly Dictionary<string, List<string>> SeedRolePermissions = new()
    {
        [SeedRoles.Owner] = [Superuser],
        [SeedRoles.Admin] =
        [
            GlucoseReadWrite, TreatmentsReadWrite, DevicesReadWrite,
            HeartRateReadWrite, StepCountReadWrite, SleepReadWrite, FoodReadWrite,
            ReportsRead,
            TherapyReadWrite, AlertsReadWrite,
            IdentityRead,
            MembersInvite, MembersManage, TenantSettings, RolesManage, SharingManage, SharingGuest,
            AuditRead,
            DeviceNotify, DeviceActuate,
        ],
        [SeedRoles.Caretaker] =
        [
            GlucoseRead, TreatmentsReadWrite, DevicesRead,
            FoodRead, HeartRateRead, StepCountRead, SleepRead,
            ReportsRead,
            TherapyRead, AlertsReadWrite,
            DeviceNotify, DeviceActuate,
        ],
        [SeedRoles.Clinician] =
        [
            GlucoseRead, TreatmentsRead, DevicesRead,
            FoodRead, HeartRateRead, StepCountRead, SleepRead,
            ReportsRead,
            TherapyRead, AlertsRead,
            DeviceNotify, DeviceActuate,
        ],
        [SeedRoles.Viewer] = [GlucoseRead, ReportsRead, DeviceNotify, DeviceActuate],
        [SeedRoles.Denied] = [],
    };

    /// <summary>
    /// Display names for seed roles.
    /// </summary>
    public static readonly Dictionary<string, string> SeedRoleNames = new()
    {
        [SeedRoles.Owner] = "Owner",
        [SeedRoles.Admin] = "Administrator",
        [SeedRoles.Caretaker] = "Caretaker",
        [SeedRoles.Viewer] = "Viewer",
        [SeedRoles.Clinician] = "Clinician",
        [SeedRoles.Denied] = "Denied",
    };

    /// <summary>
    /// Checks if a permission satisfies a required permission.
    /// Handles readwrite implying read, and <see cref="Superuser"/> satisfying everything.
    /// </summary>
    /// <param name="granted">The permission that has been granted.</param>
    /// <param name="required">The permission that is required.</param>
    /// <returns><c>true</c> if <paramref name="granted"/> satisfies <paramref name="required"/>.</returns>
    public static bool Satisfies(string granted, string required)
    {
        if (granted == Superuser) return true;
        if (granted == required) return true;
        // readwrite implies read
        if (required.EndsWith(".read") && granted == required.Replace(".read", ".readwrite"))
            return true;
        // audit.manage implies audit.read
        if (required == AuditRead && granted == AuditManage)
            return true;
        return false;
    }

    /// <summary>
    /// Checks if a set of permissions satisfies a required permission.
    /// </summary>
    /// <param name="permissions">The set of granted permissions to check against.</param>
    /// <param name="required">The permission that is required.</param>
    /// <returns><c>true</c> if any permission in <paramref name="permissions"/> satisfies <paramref name="required"/>.</returns>
    public static bool HasPermission(IEnumerable<string> permissions, string required)
    {
        return permissions.Any(p => Satisfies(p, required));
    }

    /// <summary>
    /// Validates a set of permissions a caller is trying to grant (to a member, a role, or an
    /// invite) against the permissions the caller itself holds. Every requested atom must be a
    /// known permission and must be satisfied by <paramref name="granterScopes"/>, so a grant can
    /// never exceed the granter's own access. <see cref="Superuser"/> is satisfied only by
    /// <see cref="Superuser"/>, so it cannot be minted by a non-superuser.
    /// </summary>
    /// <param name="requested">The permissions being granted. <c>null</c> or empty is always allowed.</param>
    /// <param name="granterScopes">
    /// The granting caller's resolved scopes. Callers pass a scope set rather than a permission set:
    /// the two vocabularies are deliberately separate, and this works because
    /// <c>OAuthScopes.MemberGrantableScopes</c> is a superset of <see cref="All"/>, so every
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
            if (permission != Superuser && !All.Contains(permission))
            {
                return new GrantCeilingViolation(
                    GrantCeilingViolation.UnknownPermission,
                    $"'{permission}' is not a known permission.");
            }

            if (!HasPermission(granter, permission))
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
