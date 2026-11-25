using Nocturne.API.Extensions;
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

    /// <summary>
    /// Whether the caller of <paramref name="httpContext"/> holds standing over the instance: a
    /// platform admin (<see cref="Nocturne.Core.Models.Authorization.AuthContext.IsPlatformAdmin"/>,
    /// which the instance key carries), or a caller on the instance's only tenant
    /// (<see cref="IsOnlyTenantAsync"/>). It does not judge the caller's access on that tenant, which
    /// the action requires on its own.
    /// </summary>
    public static async Task<bool> HeldByAsync(HttpContext httpContext)
    {
        if (httpContext.GetAuthContext()?.IsPlatformAdmin == true)
            return true;

        var tenant = httpContext.RequestServices.GetRequiredService<ITenantAccessor>();
        return tenant.IsResolved
            && await IsOnlyTenantAsync(
                httpContext.RequestServices.GetRequiredService<ITenantRepository>(), tenant.TenantId, httpContext.RequestAborted);
    }
}
