using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Nocturne.Core.Models;
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
    /// <returns>The number of rows deleted, links not counted.</returns>
    public static async Task<int> PurgeAsync<TEntity>(
        this DbSet<TEntity> rows,
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken ct = default)
        where TEntity : class, ITenantScoped
    {
        var purgeable = rows.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]);
        var selected = predicate is null ? purgeable : purgeable.Where(predicate);

        // A deduplicated row's links go first: a purge that stops between the two statements then
        // leaves a record with no link, which matching reads as a group of its own, rather than a
        // link with no record (see Services.DeduplicationService.DeleteOrphanedLinksAsync).
        if (DuplicateGroupPrimaries.RecordTypeOf<TEntity>() is { } recordType)
        {
            var key = RecordTypeKeys.Key(recordType);
            var context = (NocturneDbContext)rows.GetService<ICurrentDbContext>().Context;
            var purgedIds = selected.Select(e => EF.Property<Guid>(e, "Id"));
            await context.LinkedRecords
                .Where(lr => lr.RecordType == key && purgedIds.Contains(lr.RecordId))
                .ExecuteDeleteAsync(ct);
        }

        return await selected.ExecuteDeleteAsync(ct);
    }
}
