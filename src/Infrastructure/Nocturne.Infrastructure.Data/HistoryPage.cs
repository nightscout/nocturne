using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Selects one page of a modified-since history read so the page ends on a millisecond boundary.
/// </summary>
/// <remarks>
/// Stamps carry sub-millisecond precision, but a history cursor is the page's newest stamp truncated
/// to milliseconds and the next request asks for stamps at or after the following millisecond. A
/// page cut inside a millisecond would either re-send that millisecond's rows, because those after
/// the cut were never seen and the cursor did not move past them, or, when one millisecond holds at
/// least <c>limit</c> rows, never advance at all. The rule:
/// <list type="number">
/// <item>fetch rows with <c>stamp &gt;= FromUnixTimeMilliseconds(cursor + 1)</c>, ordered by
/// <c>(stamp, id)</c>, take <c>limit</c>;</item>
/// <item>if <c>limit</c> rows came back, extend the page to the end of its last row's millisecond
/// with a second read over that millisecond, adding only rows the first read did not carry. The
/// page may therefore exceed <c>limit</c>, with no cap;</item>
/// <item>log a warning when it does, naming the table, the millisecond and the row count.</item>
/// </list>
/// A single millisecond can only hold up to <see cref="NocturneDbContext.SystemTimestampGroupSize"/>
/// rows of one type, because the write path spreads a bulk save across successive milliseconds, and
/// controllers clamp <c>limit</c> to the same size, so the extension adds at most one page.
/// Both bounds are plain range predicates on the stamp column, served on every
/// <see cref="NocturneDbContext.V4HistoryPagedEntities"/> table by its tenant-leading stamp index.
/// </remarks>
public static class HistoryPage
{
    public static async Task<List<T>> GetAsync<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime>> stamp,
        Expression<Func<T, Guid>> id,
        long cursorMills,
        int limit,
        ILogger logger,
        string table,
        CancellationToken ct)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(cursorMills + 1).UtcDateTime;
        var page = await source
            .Where(InRange(stamp, from, null))
            .OrderBy(stamp)
            .ThenBy(id)
            .Take(limit)
            .ToListAsync(ct);

        if (page.Count == 0 || page.Count < limit)
            return page;

        var idOf = id.Compile();
        var seen = page.Select(idOf).ToHashSet();
        var lastMills = ToMilliseconds(stamp.Compile()(page[^1]));

        foreach (var row in await GetMillisecondAsync(source, stamp, id, lastMills, ct))
        {
            if (seen.Add(idOf(row)))
                page.Add(row);
        }

        if (page.Count > limit)
        {
            logger.LogWarning(
                "History page for {Table} ended inside millisecond {Millisecond}: {Count} rows for a limit of {Limit}",
                table,
                lastMills,
                page.Count,
                limit);
        }

        return page;
    }

    /// <summary>
    /// Every row whose stamp falls in <c>[millisecond, millisecond + 1)</c>, ordered by
    /// <c>(stamp, id)</c>.
    /// </summary>
    private static Task<List<T>> GetMillisecondAsync<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime>> stamp,
        Expression<Func<T, Guid>> id,
        long millisecond,
        CancellationToken ct)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(millisecond).UtcDateTime;
        return source
            .Where(InRange(stamp, from, from.AddMilliseconds(1)))
            .OrderBy(stamp)
            .ThenBy(id)
            .ToListAsync(ct);
    }

    public static long ToMilliseconds(DateTime stamp) =>
        new DateTimeOffset(stamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static Expression<Func<T, bool>> InRange<T>(
        Expression<Func<T, DateTime>> stamp,
        DateTime? from,
        DateTime? to)
    {
        Expression body = from is { } lower
            ? Expression.GreaterThanOrEqual(stamp.Body, Expression.Constant(lower, typeof(DateTime)))
            : Expression.Constant(true);

        if (to is { } upper)
        {
            var lessThan = Expression.LessThan(stamp.Body, Expression.Constant(upper, typeof(DateTime)));
            body = from is null ? lessThan : Expression.AndAlso(body, lessThan);
        }

        return Expression.Lambda<Func<T, bool>>(body, stamp.Parameters[0]);
    }
}
