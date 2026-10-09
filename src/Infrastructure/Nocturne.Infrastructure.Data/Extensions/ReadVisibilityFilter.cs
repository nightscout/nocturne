using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the rows a read shows once deduplication has run".
/// </summary>
/// <remarks>
/// A count has to apply the same predicate as the read it paginates, or totals are inflated by
/// duplicates.
/// </remarks>
public static class ReadVisibilityFilter
{
    /// <summary>Drops the rows <paramref name="recordType"/>'s links mark non-primary.</summary>
    public static IQueryable<TEntity> ExcludeNonPrimary<TEntity>(
        this IQueryable<TEntity> query,
        NocturneDbContext ctx,
        RecordType recordType)
        where TEntity : IIdentified
    {
        var key = RecordTypeKeys.Key(recordType);
        // Keep the correlated Any (NOT EXISTS). !ids.Contains(e.Id) emits NOT IN over a subquery,
        // which PostgreSQL does not anti-join, and past work_mem it falls back to per-row rescans.
        return query.Where(e =>
            !ctx.LinkedRecords.Any(lr => lr.RecordType == key && !lr.IsPrimary && lr.RecordId == e.Id));
    }

    /// <summary>
    /// <see cref="ExcludeNonPrimary{TEntity}"/> over live rows only. A soft-deleted row always passes:
    /// a v3 history read has to deliver its delete whatever deduplication later decided about it.
    /// </summary>
    public static IQueryable<TEntity> ExcludeNonPrimaryKeepingDeleted<TEntity>(
        this IQueryable<TEntity> query,
        NocturneDbContext ctx,
        RecordType recordType)
        where TEntity : IIdentified, ISoftDeletable
    {
        var key = RecordTypeKeys.Key(recordType);
        return query.Where(e =>
            e.DeletedAt != null
            || !ctx.LinkedRecords.Any(lr => lr.RecordType == key && !lr.IsPrimary && lr.RecordId == e.Id));
    }
}
