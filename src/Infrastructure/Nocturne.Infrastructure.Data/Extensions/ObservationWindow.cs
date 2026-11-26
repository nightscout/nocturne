using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>The bounds of an overlap window that a span may touch without overlapping it.</summary>
[Flags]
public enum OverlapEdges
{
    /// <summary>A span that starts at the window's end, or ends at its start, overlaps it.</summary>
    Closed = 0,

    /// <summary>A span that starts at the window's end does not overlap it.</summary>
    StartAtToExcluded = 1,

    /// <summary>A span that ends at the window's start does not overlap it.</summary>
    EndAtFromExcluded = 2,
}

/// <summary>What a span with no end means to an overlap window.</summary>
public enum OpenSpanRule
{
    /// <summary>An open span is read when it started by the window's end.</summary>
    Overlaps,

    /// <summary>An open span is read wherever it started.</summary>
    AlwaysRead,

    /// <summary>An open span is read when it started inside the window, not when it started before it.</summary>
    OnlyWhenStartedInWindow,
}

/// <summary>The one time window every record read and delete over an observation time or a span shares.</summary>
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

    /// <summary>
    /// The rows whose <paramref name="at"/> is at or after <paramref name="from"/> and before
    /// <paramref name="to"/>, either optional: the window a cursor-paged read takes, so the next page
    /// starts at the instant this one stopped before.
    /// </summary>
    public static IQueryable<TEntity> InHalfOpenWindow<TEntity, TValue>(
        this IQueryable<TEntity> rows, Expression<Func<TEntity, TValue>> at, TValue? from, TValue? to)
        where TValue : struct
    {
        if (from.HasValue)
            rows = rows.Where(Compare(at, () => from.Value, Expression.GreaterThanOrEqual));
        if (to.HasValue)
            rows = rows.Where(Compare(at, () => to.Value, Expression.LessThan));
        return rows;
    }

    /// <summary>
    /// The spans that overlap the window: they end at or after <paramref name="from"/> and start at or
    /// before <paramref name="to"/>, either bound optional. <paramref name="edges"/> names the bounds a
    /// read excludes, and <paramref name="open"/> what a span with no end means.
    /// </summary>
    public static IQueryable<TEntity> OverlapsWindow<TEntity, TValue>(
        this IQueryable<TEntity> rows,
        Expression<Func<TEntity, TValue>> start,
        Expression<Func<TEntity, TValue?>> end,
        TValue? from,
        TValue? to,
        OverlapEdges edges = OverlapEdges.Closed,
        OpenSpanRule open = OpenSpanRule.Overlaps)
        where TValue : struct
    {
        var endsInWindow = from.HasValue ? EndsInWindow(end, () => from.Value, edges) : null;
        var startsInWindow = to.HasValue ? StartsInWindow(start, () => to.Value, edges) : null;
        var isOpen = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(end.Body, Expression.Constant(null, typeof(TValue?))), end.Parameters);

        switch (open)
        {
            case OpenSpanRule.AlwaysRead:
                var bounded = And(endsInWindow, startsInWindow);
                return bounded is null ? rows : rows.Where(Combine(Expression.OrElse, isOpen, bounded)!);
            case OpenSpanRule.OnlyWhenStartedInWindow:
                if (endsInWindow is not null)
                {
                    var startedInWindow = Compare(start, () => from.Value, Expression.GreaterThanOrEqual);
                    rows = rows.Where(Combine(Expression.OrElse, endsInWindow, And(isOpen, startedInWindow))!);
                }
                break;
            default:
                if (endsInWindow is not null)
                    rows = rows.Where(Combine(Expression.OrElse, isOpen, endsInWindow)!);
                break;
        }

        return startsInWindow is null ? rows : rows.Where(startsInWindow);
    }

    /// <summary>
    /// The spans that overlap the window, for a span whose end cannot be absent: the bounds of the
    /// overload for spans that may be open, without its open-span rule.
    /// </summary>
    public static IQueryable<TEntity> OverlapsWindow<TEntity, TValue>(
        this IQueryable<TEntity> rows,
        Expression<Func<TEntity, TValue>> start,
        Expression<Func<TEntity, TValue>> end,
        TValue? from,
        TValue? to,
        OverlapEdges edges = OverlapEdges.Closed)
        where TValue : struct
    {
        if (from.HasValue)
            rows = rows.Where(EndsInWindow(end, () => from.Value, edges));
        if (to.HasValue)
            rows = rows.Where(StartsInWindow(start, () => to.Value, edges));
        return rows;
    }

    private static Expression<Func<TEntity, bool>> EndsInWindow<TEntity, TEnd, TValue>(
        Expression<Func<TEntity, TEnd>> end, Expression<Func<TValue>> from, OverlapEdges edges) =>
        Compare(end, from, edges.HasFlag(OverlapEdges.EndAtFromExcluded) ? Expression.GreaterThan : Expression.GreaterThanOrEqual);

    private static Expression<Func<TEntity, bool>> StartsInWindow<TEntity, TStart, TValue>(
        Expression<Func<TEntity, TStart>> start, Expression<Func<TValue>> to, OverlapEdges edges) =>
        Compare(start, to, edges.HasFlag(OverlapEdges.StartAtToExcluded) ? Expression.LessThan : Expression.LessThanOrEqual);

    private static Expression<Func<TEntity, bool>>? And<TEntity>(
        Expression<Func<TEntity, bool>>? left, Expression<Func<TEntity, bool>>? right) =>
        left is null ? right : right is null ? left : Combine(Expression.AndAlso, left, right);

    private static Expression<Func<TEntity, bool>>? Combine<TEntity>(
        Func<Expression, Expression, BinaryExpression> combine,
        Expression<Func<TEntity, bool>>? left,
        Expression<Func<TEntity, bool>>? right) =>
        left is null || right is null
            ? left ?? right
            : Expression.Lambda<Func<TEntity, bool>>(
                combine(left.Body, ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body)),
                left.Parameters);

    // The bound is read off a closure so EF sends it as a parameter, not a literal per window.
    private static Expression<Func<TEntity, bool>> Compare<TEntity, TAt, TValue>(
        Expression<Func<TEntity, TAt>> at,
        Expression<Func<TValue>> bound,
        Func<Expression, Expression, BinaryExpression> compare) =>
        Expression.Lambda<Func<TEntity, bool>>(
            compare(at.Body, typeof(TAt) == typeof(TValue) ? bound.Body : Expression.Convert(bound.Body, typeof(TAt))),
            at.Parameters);
}
