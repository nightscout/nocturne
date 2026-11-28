using System.Linq.Expressions;
using System.Reflection;
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

    /// <summary>A span overlaps only where it shares a positive length with the window: one that only touches a bound does not.</summary>
    BothExcluded = StartAtToExcluded | EndAtFromExcluded,
}

/// <summary>Whether a span that ends exactly at the instant is active at it.</summary>
public enum ActiveEnd
{
    /// <summary>A span is active on <c>[start, end)</c>: at its end instant it is over.</summary>
    Exclusive,

    /// <summary>A span is active on <c>[start, end]</c>: it still counts at its end instant.</summary>
    Inclusive,
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

    private static readonly MethodInfo ListContains = typeof(List<DateTime>).GetMethod(nameof(List<DateTime>.Contains))!;

    /// <summary>
    /// The rows of a record table whose event time (<see cref="V4RecordTables.EventTimeOf{T}"/>) is
    /// exactly one of <paramref name="instants"/>. A record time is stored at the millisecond, so an
    /// instant at that precision names the row's time and no window is needed.
    /// </summary>
    public static IQueryable<TEntity> AtInstants<TEntity>(this IQueryable<TEntity> rows, IReadOnlyCollection<DateTime> instants)
        where TEntity : class, ITenantScoped, ISoftDeletable, IIdentified, IWriterStamped
    {
        var at = instants.ToList();
        Expression<Func<List<DateTime>>> bound = () => at;
        var eventTime = V4RecordTables.EventTimeOf<TEntity>();
        var contains = Expression.Call(bound.Body, ListContains, eventTime.Body);
        return rows.Where(Expression.Lambda<Func<TEntity, bool>>(contains, eventTime.Parameters));
    }

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
        var isOpen = IsOpen(end);

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

    /// <summary>
    /// The spans active at <paramref name="at"/>: started at or before it, and open or ending after it.
    /// <paramref name="endAt"/> names whether a span ending exactly at it is active.
    /// </summary>
    public static IQueryable<TEntity> ActiveAt<TEntity, TValue>(
        this IQueryable<TEntity> rows,
        Expression<Func<TEntity, TValue>> start,
        Expression<Func<TEntity, TValue?>> end,
        TValue at,
        ActiveEnd endAt = ActiveEnd.Exclusive)
        where TValue : struct
    {
        Expression<Func<TValue>> instant = () => at;
        var started = Compare(start, instant, Expression.LessThanOrEqual);
        var notEnded = NotEnded(end, instant, endAt == ActiveEnd.Inclusive ? Expression.GreaterThanOrEqual : Expression.GreaterThan);
        return rows.Where(And(started, notEnded)!);
    }

    /// <summary>The spans that are open or end after <paramref name="at"/>: a span ending exactly at it has ended.</summary>
    public static IQueryable<TEntity> NotEndedBy<TEntity, TValue>(
        this IQueryable<TEntity> rows, Expression<Func<TEntity, TValue?>> end, TValue at)
        where TValue : struct
    {
        Expression<Func<TValue>> instant = () => at;
        return rows.Where(NotEnded(end, instant, Expression.GreaterThan));
    }

    private static Expression<Func<TEntity, bool>> NotEnded<TEntity, TValue>(
        Expression<Func<TEntity, TValue?>> end,
        Expression<Func<TValue>> instant,
        Func<Expression, Expression, BinaryExpression> endsAfter)
        where TValue : struct =>
        Combine(Expression.OrElse, IsOpen(end), Compare(end, instant, endsAfter))!;

    private static Expression<Func<TEntity, bool>> IsOpen<TEntity, TValue>(Expression<Func<TEntity, TValue?>> end)
        where TValue : struct =>
        Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(end.Body, Expression.Constant(null, typeof(TValue?))), end.Parameters);

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
