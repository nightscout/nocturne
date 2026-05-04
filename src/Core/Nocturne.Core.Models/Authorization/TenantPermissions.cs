namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Permission atoms for the tenant RBAC system.
/// Uses the resource.action format compatible with <see cref="OAuthScopes"/>.
/// </summary>
/// <seealso cref="OAuthScopes"/>
/// <seealso cref="Role"/>
/// <seealso cref="ScopeTranslator"/>
public static class TenantPermissions
{
    // Data permissions (existing OAuth scopes)

    /// <summary>Read-only access to glucose entries within the tenant.</summary>
    public const string EntriesRead = "entries.read";
    /// <summary>Read and write access to glucose entries within the tenant.</summary>
    public const string EntriesReadWrite = "entries.readwrite";
    /// <summary>Read-only access to treatments within the tenant.</summary>
    public const string TreatmentsRead = "treatments.read";
    /// <summary>Read and write access to treatments within the tenant.</summary>
    public const string TreatmentsReadWrite = "treatments.readwrite";
    /// <summary>Read-only access to device status records within the tenant.</summary>
    public const string DeviceStatusRead = "devicestatus.read";
    /// <summary>Read and write access to device status records within the tenant.</summary>
    public const string DeviceStatusReadWrite = "devicestatus.readwrite";
    /// <summary>Read-only access to user profiles within the tenant.</summary>
    public const string ProfileRead = "profile.read";
    /// <summary>Read and write access to user profiles within the tenant.</summary>
    public const string ProfileReadWrite = "profile.readwrite";
    /// <summary>Read-only access to notification settings within the tenant.</summary>
    public const string NotificationsRead = "notifications.read";
    /// <summary>Read and write access to notification settings within the tenant.</summary>
    public const string NotificationsReadWrite = "notifications.readwrite";
    /// <summary>Read-only access to generated reports within the tenant.</summary>
    public const string ReportsRead = "reports.read";
    /// <summary>Read-only access to aggregated health data within the tenant.</summary>
    public const string HealthRead = "health.read";
    /// <summary>Read-only access to identity information within the tenant.</summary>
    public const string IdentityRead = "identity.read";

    // Feature/admin permissions (new)

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
    /// <summary>Read-only access to the mutation audit log.</summary>
    public const string AuditRead = "audit.read";
    /// <summary>Permission to manage audit settings (retention, export).</summary>
    public const string AuditManage = "audit.manage";
    /// <summary>Permission to create temporary guest access links.</summary>
    public const string SharingGuest = "sharing.guest";
    /// <summary>Superuser permission that satisfies all other permissions.</summary>
    public const string Superuser = "*";

    /// <summary>
    /// All valid permission atoms (excluding superuser).
    /// </summary>
    public static readonly HashSet<string> All =
    [
        EntriesRead, EntriesReadWrite,
        TreatmentsRead, TreatmentsReadWrite,
        DeviceStatusRead, DeviceStatusReadWrite,
        ProfileRead, ProfileReadWrite,
        NotificationsRead, NotificationsReadWrite,
        ReportsRead,
        HealthRead,
        IdentityRead,
        RolesManage,
        MembersInvite,
        MembersManage,
        TenantSettings,
        SharingManage,
        AuditRead,
        AuditManage,
        SharingGuest,
    ];

    /// <summary>
    /// Seed role slugs.
    /// </summary>
    public static class SeedRoles
    {
        public const string Owner = "owner";
        public const string Admin = "admin";
        public const string Caretaker = "caretaker";
        public const string Follower = "follower";
        public const string Readable = "readable";
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
            EntriesReadWrite, TreatmentsReadWrite, DeviceStatusReadWrite,
            ProfileReadWrite, NotificationsReadWrite, ReportsRead,
            HealthRead, IdentityRead,
            MembersInvite, MembersManage, TenantSettings, RolesManage, SharingManage, SharingGuest,
            AuditRead,
        ],
        [SeedRoles.Caretaker] =
        [
            EntriesRead, TreatmentsReadWrite, DeviceStatusRead,
            ProfileRead, NotificationsRead, ReportsRead, HealthRead,
        ],
        [SeedRoles.Follower] = [EntriesRead, HealthRead],
        [SeedRoles.Readable] =
        [
            EntriesRead, TreatmentsRead, DeviceStatusRead,
            ProfileRead, HealthRead,
        ],
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
        [SeedRoles.Follower] = "Follower",
        [SeedRoles.Readable] = "Readable",
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
