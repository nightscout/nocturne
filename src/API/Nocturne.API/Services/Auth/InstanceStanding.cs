using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Whether full access on a tenant is standing over the whole instance, for a write to something
/// every tenant reads, such as the global roles.
/// </summary>
internal static class InstanceStanding
{
    /// <summary>
    /// Whether <paramref name="tenantId"/> is the instance's only tenant, counted as
    /// <see cref="PlatformAdminBootstrapService.EnsureFirstOwnerIsPlatformAdminAsync"/> counts it: a
    /// deactivated tenant counts, a demo tenant does not.
    /// </summary>
    public static async Task<bool> IsOnlyTenantAsync(ITenantRepository tenants, Guid tenantId, CancellationToken ct)
    {
        var listed = await tenants.ListNonDemoAsync(2, ct);
        return listed.Count == 1 && listed[0].Id == tenantId;
    }
}
