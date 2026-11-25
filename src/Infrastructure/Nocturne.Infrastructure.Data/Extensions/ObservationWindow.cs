using System.Linq.Expressions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>The one inclusive time window every record read and delete over an observation time shares.</summary>
public static class ObservationWindow
{
    /// <summary>
    /// The rows whose <see cref="IObservationTimestamped.Timestamp"/> is inside the window, bounds
    /// inclusive and either optional.
    /// </summary>
    public static IQueryable<TEntity> InWindow<TEntity>(this IQueryable<TEntity> rows, DateTime? from, DateTime? to)
        where TEntity : IObservationTimestamped =>
        rows.InWindow(e => e.Timestamp, from, to);

    /// <summary>
    /// The rows whose <paramref name="at"/> is inside the window, bounds inclusive and either optional,
    /// for a column other than <see cref="IObservationTimestamped.Timestamp"/>, or an entity without
    /// that interface.
    /// </summary>
    public static IQueryable<TEntity> InWindow<TEntity, TValue>(
        this IQueryable<TEntity> rows, Expression<Func<TEntity, TValue>> at, TValue? from, TValue? to)
        where TValue : struct
    {
        if (from.HasValue)
            rows = rows.Where(Compare(at, () => from.Value, Expression.GreaterThanOrEqual));
        if (to.HasValue)
            rows = rows.Where(Compare(at, () => to.Value, Expression.LessThanOrEqual));
        return rows;
    }

    // The bound is read off a closure so EF sends it as a parameter, not a literal per window.
    private static Expression<Func<TEntity, bool>> Compare<TEntity, TValue>(
        Expression<Func<TEntity, TValue>> at,
        Expression<Func<TValue>> bound,
        Func<Expression, Expression, BinaryExpression> compare) =>
        Expression.Lambda<Func<TEntity, bool>>(compare(at.Body, bound.Body), at.Parameters);
}
