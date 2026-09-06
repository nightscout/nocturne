using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one reading of "this install has exactly one tenant, so the apex domain serves it".
/// </summary>
/// <remarks>
/// Tenant resolution, the Scalar docs resolver and the dev-only session login answer the same
/// question at the apex and must answer it identically, or the front door serves one tenant for
/// requests and another for docs. An inactive tenant is not served and the demo is never the
/// operator's (<see cref="DemoExclusionFilter"/>), so neither counts. First-run setup and the
/// platform-admin grant ask a different question, whether this is a fresh install, which an
/// inactive tenant still answers, so they count tenants themselves.
/// </remarks>
public static class SoleTenantQuery
{
    /// <summary>
    /// The install's only servable tenant, or <see langword="null"/> when none or several exist.
    /// </summary>
    public static async Task<TenantEntity?> SoleTenantAsync(
        this DbSet<TenantEntity> tenants, CancellationToken ct = default)
    {
        var candidates = await tenants.AsNoTracking().ExcludeDemo().Where(t => t.IsActive).Take(2).ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
