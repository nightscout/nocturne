using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Decorates the pooled <see cref="IDbContextFactory{TContext}"/> so every lease of a
/// <see cref="NocturneDbContext"/> starts from fail-closed carrier defaults.
///
/// <para>
/// <see cref="NocturneDbContext"/> is pooled (<c>AddPooledDbContextFactory</c>), and pooling
/// does not reset custom properties between leases. A context returned to the pool therefore
/// keeps the previous lessee's <see cref="NocturneDbContext.TenantId"/>,
/// <see cref="NocturneDbContext.AuditContext"/>, <see cref="NocturneDbContext.IsShareContext"/>
/// and <see cref="NocturneDbContext.VisibleCategories"/>. The <c>ITenantDbContextFactory</c> and
/// scoped-context registration already stamp those on acquisition; the callers that take the raw
/// factory and call <see cref="IDbContextFactory{TContext}.CreateDbContext"/> directly do not, so
/// a context that last served a public share could carry <c>IsShareContext = true</c> into a later
/// lease — and the per-category share RLS policy denies that lease's reads of any tenant-scoped
/// table (a silent, fail-closed lockout), while a stale non-empty <see cref="NocturneDbContext.TenantId"/>
/// could expose another tenant's rows.
/// </para>
///
/// <para>
/// This single chokepoint sits in front of every acquisition path (both
/// <c>ITenantDbContextFactory</c> and the scoped registration resolve the same factory), so the
/// raw callers are made safe without each having to reset the carrier. Callers that need real
/// values pin them after acquisition; the reset only establishes the safe defaults.
/// </para>
/// </summary>
internal sealed class CarrierResettingDbContextFactory(IDbContextFactory<NocturneDbContext> inner)
    : IDbContextFactory<NocturneDbContext>
{
    public NocturneDbContext CreateDbContext() => Reset(inner.CreateDbContext());

    public async ValueTask<NocturneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Reset(await inner.CreateDbContextAsync(cancellationToken));

    private static NocturneDbContext Reset(NocturneDbContext context)
    {
        context.TenantId = Guid.Empty;
        context.AuditContext = null;
        context.IsShareContext = false;
        context.VisibleCategories = null;
        return context;
    }
}
