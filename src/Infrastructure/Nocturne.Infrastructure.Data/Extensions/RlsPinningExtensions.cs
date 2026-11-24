using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Pins a context whose connection may already be open.
/// </summary>
public static class RlsPinningExtensions
{
    /// <summary>
    /// Pins <paramref name="context"/> to <paramref name="tenantId"/> and writes the
    /// <c>app.current_tenant_id</c> GUC and the history tag (<see cref="HistoryHorizon.SessionTag"/>)
    /// on its current connection, for a tenant known only once
    /// the connection is open (see <see cref="TenantReadContext"/>). A tenant known before the
    /// first query comes from <see cref="INocturneDbContextFactory.CreateForTenantAsync"/>.
    /// </summary>
    /// <param name="context">The context to pin.</param>
    /// <param name="tenantId">The tenant whose rows the caller is entitled to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    /// The GUC write is skipped on non-PostgreSQL providers (the SQLite and in-memory providers
    /// used by unit tests), where there is no Row Level Security and <c>set_config</c> does not
    /// exist.
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
            "SELECT set_config('app.current_tenant_id', {0}, false), set_config('application_name', {1}, false)",
            [tenantId.ToString(), HistoryHorizon.SessionTag(tenantId)],
            ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="context"/> pinned to <paramref name="tenantId"/>,
    /// its query filter and its <c>app.current_tenant_id</c> GUC both, the GUC local to the open
    /// transaction, then puts back the tenant it had. <see cref="Guid.Empty"/> lifts the pin, which every
    /// policy reads as unset. For one transaction that reads or writes the rows of several tenants, each
    /// under its own; what <paramref name="work"/> stages on a table whose policy holds it to one tenant
    /// is saved inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public static async Task<T> InTenantForTransactionAsync<T>(
        this NocturneDbContext context, Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var before = context.TenantId;
        try
        {
            await SetTenantForTransactionAsync(context, tenantId, ct);
            var result = await work(ct);
            await SetTenantForTransactionAsync(context, before, ct);
            return result;
        }
        finally
        {
            context.TenantId = before;
        }
    }

    private static async Task SetTenantForTransactionAsync(NocturneDbContext context, Guid tenantId, CancellationToken ct)
    {
        context.TenantId = tenantId;
        if (!context.Database.IsNpgsql())
            return;
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A tenant is pinned for a transaction inside one");

        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, true)",
            [tenantId == Guid.Empty ? string.Empty : tenantId.ToString()],
            ct);
    }
}
