using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Decorates the <see cref="IDbContextFactory{TContext}"/> so every acquisition of a
/// <see cref="NocturneDbContext"/> starts from fail-closed carrier defaults.
///
/// <para>
/// The context carries per-request state — <see cref="NocturneDbContext.TenantId"/>,
/// <see cref="NocturneDbContext.AuditContext"/>, <see cref="NocturneDbContext.IsShareContext"/>
/// and <see cref="NocturneDbContext.VisibleCategories"/> — that the RLS policy reads. The
/// <c>ITenantDbContextFactory</c> and scoped-context registration stamp those on acquisition;
/// callers that take the raw factory and call <see cref="IDbContextFactory{TContext}.CreateDbContext"/>
/// directly do not. Normalizing to safe defaults here guarantees that, however a context is
/// obtained, a missing stamp fails closed: a null category CSV denies categorized reads rather
/// than exposing them, and an empty <see cref="NocturneDbContext.TenantId"/> matches no tenant's rows.
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
