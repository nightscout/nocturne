namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Instance-wide roles a subject holds outside any tenant, as opposed to the per-tenant
/// <see cref="RoleSeeds"/>.
/// </summary>
public static class GlobalRoles
{
    /// <summary>Full administrative access, which first-run setup gives its owner.</summary>
    public const string Admin = "admin";
}
