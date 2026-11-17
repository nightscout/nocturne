using System.Linq.Expressions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the rows a read shows once deduplication has run": every row but the
/// non-primary copies <see cref="IDuplicateFlagged.IsDuplicate"/> marks.
/// </summary>
/// <remarks>
/// A count has to apply the same predicate as the read it paginates, or totals are inflated by
/// duplicates. The hot read indexes of the flagged tables are partial on <c>NOT is_duplicate</c>,
/// so a read that drops this filter also loses its index.
/// </remarks>
public static class ReadVisibilityFilter
{
    /// <summary>Drops the non-primary copies of duplicate groups.</summary>
    public static IQueryable<TEntity> ExcludeDuplicates<TEntity>(this IQueryable<TEntity> query)
        where TEntity : IDuplicateFlagged =>
        query.Where(e => !e.IsDuplicate);

    /// <summary>
    /// <see cref="ExcludeDuplicates{TEntity}"/> over live rows only. A soft-deleted row always passes:
    /// a v3 history read has to deliver its delete whatever deduplication later decided about it.
    /// </summary>
    public static IQueryable<TEntity> ExcludeDuplicatesKeepingDeleted<TEntity>(this IQueryable<TEntity> query)
        where TEntity : IDuplicateFlagged, ISoftDeletable =>
        query.Where(e => e.DeletedAt != null || !e.IsDuplicate);

    /// <summary>
    /// <see cref="ExcludeDuplicates{TEntity}"/> for a query whose entity type is known only at run
    /// time to be flagged; a type deduplication does not link passes through unchanged.
    /// </summary>
    public static IQueryable<TEntity> ExcludeDuplicatesWhereFlagged<TEntity>(this IQueryable<TEntity> query) =>
        Flagged<TEntity>.Visible is { } visible ? query.Where(visible) : query;

    /// <summary>
    /// <see cref="ExcludeDuplicatesKeepingDeleted{TEntity}"/> for a query whose entity type is known
    /// only at run time to be flagged.
    /// </summary>
    public static IQueryable<TEntity> ExcludeDuplicatesWhereFlaggedKeepingDeleted<TEntity>(this IQueryable<TEntity> query)
        where TEntity : ISoftDeletable =>
        Flagged<TEntity>.VisibleOrDeleted is { } visible ? query.Where(visible) : query;

    /// <summary>
    /// The two predicates for a caller whose <typeparamref name="TEntity"/> is not constrained to
    /// <see cref="IDuplicateFlagged"/>, so the flag can be bound only at run time; null for a type
    /// that is not flagged.
    /// </summary>
    private static class Flagged<TEntity>
    {
        public static readonly Expression<Func<TEntity, bool>>? Visible = Build(keepDeleted: false);

        public static readonly Expression<Func<TEntity, bool>>? VisibleOrDeleted = Build(keepDeleted: true);

        private static Expression<Func<TEntity, bool>>? Build(bool keepDeleted)
        {
            if (!typeof(IDuplicateFlagged).IsAssignableFrom(typeof(TEntity)))
                return null;

            var row = Expression.Parameter(typeof(TEntity), "e");
            Expression visible = Expression.Not(Expression.Property(row, nameof(IDuplicateFlagged.IsDuplicate)));
            if (keepDeleted)
                visible = Expression.OrElse(
                    Expression.NotEqual(
                        Expression.Property(row, nameof(ISoftDeletable.DeletedAt)),
                        Expression.Constant(null, typeof(DateTime?))),
                    visible);
            return Expression.Lambda<Func<TEntity, bool>>(visible, row);
        }
    }
}
