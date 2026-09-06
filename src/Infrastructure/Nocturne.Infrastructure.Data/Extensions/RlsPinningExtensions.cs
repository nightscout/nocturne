using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Pins a <see cref="NocturneDbContext"/> to the tenant or subject whose rows a caller is
/// entitled to, for callers that take the raw <see cref="IDbContextFactory{TContext}"/> and
/// therefore get no pin from <c>ITenantDbContextFactory</c> (singletons, background services,
/// and the tenantless entry points, which have no resolved ambient tenant to pin from).
/// </summary>
/// <remarks>
/// <para>
/// The pin has two carriers and both matter: the context property drives the EF global query
/// filters, and the <c>app.current_tenant_id</c> / <c>app.current_subject_id</c> GUCs drive the
/// PostgreSQL Row Level Security policies. <c>TenantConnectionInterceptor</c> writes the GUCs
/// from the properties when the connection opens, so pinning before the first query needs
/// nothing else — which is why the create-and-pin methods here cost no extra round-trip.
/// </para>
/// <para>
/// <see cref="PinTenantAsync"/> exists for the sites whose tenant is only known after the
/// connection is already open (provisioning reads the new tenant's id off the same context,
/// and the setup flow resolves the sole tenant before it can pin to it). There the interceptor
/// has already run, so the GUC has to be written explicitly as well.
/// </para>
/// </remarks>
public static class RlsPinningExtensions
{
    /// <summary>
    /// Creates a <see cref="NocturneDbContext"/> pinned to <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="factory">The context factory.</param>
    /// <param name="tenantId">The tenant whose rows the caller is entitled to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A tenant-pinned context. Dispose it when done (use <c>await using</c>).</returns>
    public static async ValueTask<NocturneDbContext> CreateTenantPinnedContextAsync(
        this IDbContextFactory<NocturneDbContext> factory, Guid tenantId, CancellationToken ct = default)
    {
        var context = await factory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;
        return context;
    }

    /// <summary>
    /// Creates a <see cref="NocturneDbContext"/> pinned to <paramref name="subjectId"/> for a
    /// subject-scoped read that spans tenants, e.g. enumerating the tenants a person belongs to.
    /// The pin grants reach over that subject's own rows only; it is never write reach.
    /// </summary>
    /// <param name="factory">The context factory.</param>
    /// <param name="subjectId">The subject whose own rows the caller is entitled to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A subject-pinned context. Dispose it when done (use <c>await using</c>).</returns>
    public static async ValueTask<NocturneDbContext> CreateSubjectPinnedContextAsync(
        this IDbContextFactory<NocturneDbContext> factory, Guid subjectId, CancellationToken ct = default)
    {
        var context = await factory.CreateDbContextAsync(ct);
        context.SubjectId = subjectId;
        return context;
    }

    /// <summary>
    /// Pins an existing context to <paramref name="tenantId"/>, including the GUC on the current
    /// connection so the pin also applies to a connection that is already open. Use
    /// <see cref="CreateTenantPinnedContextAsync"/> instead when the tenant is known before the
    /// context's first query.
    /// </summary>
    /// <param name="context">The context to pin.</param>
    /// <param name="tenantId">The tenant whose rows the caller is entitled to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    /// The GUC write is skipped on non-PostgreSQL providers (the SQLite and in-memory providers
    /// used by unit tests), where there is no Row Level Security and <c>set_config</c> does not
    /// exist. Those tests therefore exercise the property pin only.
    /// </remarks>
    public static async Task PinTenantAsync(
        this NocturneDbContext context, Guid tenantId, CancellationToken ct = default)
    {
        context.TenantId = tenantId;

        if (!context.Database.IsNpgsql())
        {
            return;
        }

        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            [tenantId.ToString()],
            ct);
    }
}
