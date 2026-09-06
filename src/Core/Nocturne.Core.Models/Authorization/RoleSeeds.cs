namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The roles every new tenant is seeded with, and the scopes each confers.
/// </summary>
/// <seealso cref="Scope"/>
/// <seealso cref="Role"/>
public static class RoleSeeds
{
    /// <summary>Full control of the tenant, including who else may get in.</summary>
    public const string Owner = "owner";
    /// <summary>Everything except minting another superuser.</summary>
    public const string Admin = "admin";
    /// <summary>A caregiver who may log treatments and manage alerts.</summary>
    public const string Caretaker = "caretaker";
    /// <summary>Read-only access to glucose and reports.</summary>
    public const string Viewer = "viewer";
    /// <summary>A clinician reviewing the record without changing it.</summary>
    public const string Clinician = "clinician";
    /// <summary>A member who has been denied access without being removed.</summary>
    public const string Denied = "denied";

    /// <summary>
    /// Default permissions for each seed role. Every authenticated human role lists the
    /// <see cref="Scope.DeviceNotify"/>/<see cref="Scope.DeviceActuate"/> capability grants — they
    /// control the member's own registered client devices, not the patient record — so the role
    /// editor shows them as part of the role's surface for new tenants. Enforcement does NOT depend
    /// on these atoms: seed roles are persisted per-tenant rows and <c>SeedRolesForTenantAsync</c>
    /// skips slugs that already exist, so tenants seeded before an atom was added never receive it.
    /// <c>MemberScopeMiddleware</c> therefore grants <see cref="Scope.MemberPersonalScopes"/> from
    /// the auth token alone (for members holding at least one permission).
    /// </summary>
    public static readonly Dictionary<string, List<string>> Permissions = new()
    {
        [Owner] = [Scope.FullAccess],
        [Admin] =
        [
            Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite, Scope.DevicesReadWrite,
            Scope.HeartRateReadWrite, Scope.StepCountReadWrite, Scope.SleepReadWrite, Scope.FoodReadWrite,
            Scope.ReportsRead,
            Scope.TherapyReadWrite, Scope.AlertsReadWrite,
            Scope.IdentityRead,
            Scope.MembersInvite, Scope.MembersManage, Scope.TenantSettings, Scope.RolesManage,
            Scope.SharingManage, Scope.SharingGuest,
            Scope.AuditRead,
            Scope.DeviceNotify, Scope.DeviceActuate,
        ],
        [Caretaker] =
        [
            Scope.GlucoseRead, Scope.TreatmentsReadWrite, Scope.DevicesRead,
            Scope.FoodRead, Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead,
            Scope.ReportsRead,
            Scope.TherapyRead, Scope.AlertsReadWrite,
            Scope.DeviceNotify, Scope.DeviceActuate,
        ],
        [Clinician] =
        [
            Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead,
            Scope.FoodRead, Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead,
            Scope.ReportsRead,
            Scope.TherapyRead, Scope.AlertsRead,
            Scope.DeviceNotify, Scope.DeviceActuate,
        ],
        [Viewer] = [Scope.GlucoseRead, Scope.ReportsRead, Scope.DeviceNotify, Scope.DeviceActuate],
        [Denied] = [],
    };

    /// <summary>
    /// Display names for seed roles.
    /// </summary>
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        [Owner] = "Owner",
        [Admin] = "Administrator",
        [Caretaker] = "Caretaker",
        [Viewer] = "Viewer",
        [Clinician] = "Clinician",
        [Denied] = "Denied",
    };
}
