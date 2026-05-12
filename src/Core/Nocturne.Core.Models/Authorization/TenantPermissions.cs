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
    /// <summary>Read-only access to food records within the tenant.</summary>
    public const string FoodRead = "food.read";
    /// <summary>Read and write access to food records within the tenant.</summary>
    public const string FoodReadWrite = "food.readwrite";
    /// <summary>Read-only access to aggregated statistics within the tenant.</summary>
    public const string StatisticsRead = "statistics.read";
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
        FoodRead, FoodReadWrite,
        StatisticsRead,
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
    /// Default permissions for each seed role.
    /// </summary>
    public static readonly Dictionary<string, List<string>> SeedRolePermissions = new()
    {
        [SeedRoles.Owner] = [Superuser],
        [SeedRoles.Admin] =
        [
            GlucoseReadWrite, TreatmentsReadWrite, DevicesReadWrite,
            HeartRateReadWrite, StepCountReadWrite, FoodReadWrite,
            StatisticsRead, ReportsRead,
            TherapyReadWrite, AlertsReadWrite,
            IdentityRead,
            MembersInvite, MembersManage, TenantSettings, RolesManage, SharingManage, SharingGuest,
            AuditRead,
        ],
        [SeedRoles.Caretaker] =
        [
            GlucoseRead, TreatmentsReadWrite, DevicesRead,
            FoodRead, HeartRateRead, StepCountRead,
            StatisticsRead, ReportsRead,
            TherapyRead, AlertsReadWrite,
        ],
        [SeedRoles.Clinician] =
        [
            GlucoseRead, TreatmentsRead, DevicesRead,
            FoodRead, HeartRateRead, StepCountRead,
            StatisticsRead, ReportsRead,
            TherapyRead, AlertsRead,
        ],
        [SeedRoles.Viewer] = [GlucoseRead, StatisticsRead],
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
}
