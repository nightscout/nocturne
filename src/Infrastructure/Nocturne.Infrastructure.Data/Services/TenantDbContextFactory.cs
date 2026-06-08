using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Scoped factory that creates <see cref="NocturneDbContext"/> instances with the current
/// tenant's ID pre-set, so the <see cref="Interceptors.TenantConnectionInterceptor"/> can
/// configure Row Level Security on connection open.
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>
    /// Creates a <see cref="NocturneDbContext"/> scoped to the current tenant.
    /// Dispose the returned context when done (use <c>await using</c>).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A tenant-scoped <see cref="NocturneDbContext"/>.</returns>
    ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default);
}

internal sealed class TenantDbContextFactory(
    IDbContextFactory<NocturneDbContext> pool,
    ITenantAccessor? tenantAccessor,
    ICategoryReadContext? categoryReadContext) : ITenantDbContextFactory
{
    public async ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
    {
        var ctx = await pool.CreateDbContextAsync(ct);
        if (tenantAccessor?.IsResolved == true)
            ctx.TenantId = tenantAccessor.TenantId;

        // Carry the real share flag and category CSV for this request; the CSV is resolved
        // post-auth and is carried only on this factory path. Carrier defaults are already set by
        // CarrierResettingDbContextFactory; a share whose CSV is null is denied all categorized
        // data by the RLS policy (fail-closed).
        var isShare = categoryReadContext?.IsShare == true;
        ctx.IsShareContext = isShare;
        ctx.VisibleCategories = isShare ? categoryReadContext!.VisibleCategoriesCsv : null;

        return ctx;
    }
}
