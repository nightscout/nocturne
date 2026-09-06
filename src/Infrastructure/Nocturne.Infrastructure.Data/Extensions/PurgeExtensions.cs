using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Hard-delete helper for the paths whose job is to empty a table — demo resets, idempotent
/// re-seeds, test fixtures — rather than to retire a record a user can still see.
/// </summary>
public static class PurgeExtensions
{
    /// <summary>
    /// Hard-deletes every row of the set matching <paramref name="predicate"/>, soft-deleted rows
    /// included, within the tenant the context is pinned to.
    /// </summary>
    /// <remarks>
    /// <c>ExecuteDeleteAsync</c> honours the global query filters, so a purge left under
    /// <see cref="NocturneDbContext.SoftDeleteFilterKey"/> skips rows that are already soft-deleted
    /// — leaving them invisible to reads and immune to the purge meant to remove them. Only that
    /// filter is lifted: parameterless <c>IgnoreQueryFilters()</c> would drop
    /// <see cref="NocturneDbContext.TenantFilterKey"/> with it and widen the delete across tenants.
    ///
    /// Unaudited, as the purge paths this serves have always been. Retiring a live record belongs on
    /// the audited soft-delete path (<see cref="AuditedBulkDeleteExtensions"/>) instead.
    /// </remarks>
    /// <param name="rows">The set to purge from.</param>
    /// <param name="predicate">Row filter, or <c>null</c> for the tenant's whole table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    public static Task<int> PurgeAsync<TEntity>(
        this DbSet<TEntity> rows,
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken ct = default)
        where TEntity : class, ITenantScoped
    {
        var purgeable = rows.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]);

        return (predicate is null ? purgeable : purgeable.Where(predicate)).ExecuteDeleteAsync(ct);
    }
}
