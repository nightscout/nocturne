using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     One reply to a <see cref="BackwardTimePager"/> request: the records read from it, and how many
///     the source served, which exceeds the records when some of them could not be read.
/// </summary>
public readonly record struct TimePage<T>(T[] Records, int Served);

/// <summary>
///     Pages a time-ordered legacy collection newest-first under an upper time bound.
/// </summary>
public static class BackwardTimePager
{
    /// <summary>How many times its page size a page may widen to.</summary>
    public const int MaxPageWidening = 10;

    /// <summary>
    ///     The largest count any crawl asks a source for. A slow source can time out serving more,
    ///     which fails the whole collection, so a millisecond more crowded than this is warned about
    ///     and stepped over instead.
    /// </summary>
    public const int MaxWidestPageSize = 10_000;

    /// <summary>
    ///     The widest page for a crawl paging at <paramref name="pageSize"/>:
    ///     <see cref="MaxPageWidening"/> times it, never past <see cref="MaxWidestPageSize"/>.
    /// </summary>
    public static int WidestPageSize(int pageSize) =>
        Math.Max(pageSize, Math.Min(pageSize * MaxPageWidening, MaxWidestPageSize));

    /// <summary>
    ///     How far a created_at written with a local offset can sort from its instant: real-world
    ///     UTC offsets span -12:00 to +14:00. A legacy Nightscout source stores created_at as a
    ///     string and compares it as one, so "2020-06-15T20:00:00+10:00" orders by its wall clock.
    ///     A crawl on created_at widens its opening bounds by this and filters each page back to
    ///     the true window with <see cref="CreatedAtWithin"/>, and steps on
    ///     <see cref="OldestWrittenCreatedAt{T}"/>. Only the opening bounds are widened: the page
    ///     cursor is already a wall clock the source returned, so widening it again would step over
    ///     records the source has yet to serve.
    /// </summary>
    public static readonly TimeSpan CreatedAtOffsetEnvelope = TimeSpan.FromHours(14);

    /// <summary>
    ///     Oldest created_at on a page as the source wrote it, the key the source orders and filters
    ///     by. Labelled UTC so <see cref="CreatedAtUpperBound"/> reproduces that wall clock verbatim
    ///     rather than shifting it by its offset or the host's timezone.
    /// </summary>
    public static DateTime? OldestWrittenCreatedAt<T>(IEnumerable<T> page, Func<T, string?> createdAtOf) =>
        page.Select(item => UploaderTimestamp.TryParse(createdAtOf(item), out var parsed)
                ? DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Utc)
                : (DateTime?)null)
            .Min();

    /// <summary>
    ///     Whether a created_at's instant falls inside a window. A value that will not parse is
    ///     kept: a crawl never drops records it cannot date.
    /// </summary>
    public static bool CreatedAtWithin(string? createdAt, DateTime? from, DateTime? to)
    {
        if (!UploaderTimestamp.TryParse(createdAt, out var parsed))
            return true;

        return (from is null || parsed.UtcDateTime >= from.Value)
            && (to is null || parsed.UtcDateTime <= to.Value);
    }

    /// <summary>
    ///     Streams the collection one page per iteration, so callers never hold more than a page of
    ///     a multi-year history in memory. Uploaders write several records at one millisecond, so
    ///     each full page steps the upper bound to its oldest record inclusively, and a record
    ///     served again is yielded once by id. A full page made entirely of that millisecond is
    ///     fetched again at twice the count, up to <paramref name="widestPageSize"/>. The crawl
    ///     steps past it with a warning once the page cannot widen further, or when a widened page
    ///     comes back short while still all at that millisecond, which is a source capping the
    ///     count. An unwidened short page is the end of the range. Ids are remembered from the
    ///     pages served since the bound entered its current second, since a bound can admit that
    ///     whole second. A page whose oldest record is newer than anything its bound admits sorts
    ///     differently in the source than it parses here, so the crawl warns and stops there
    ///     rather than step through it.
    /// </summary>
    /// <param name="from">Optional inclusive lower bound, below which the crawl ends.</param>
    /// <param name="to">Optional inclusive upper bound of the first page.</param>
    /// <param name="pageSize">The count every page is first asked for.</param>
    /// <param name="widestPageSize">The largest count a page crowded into one millisecond is asked for.</param>
    /// <param name="fetchPage">Fetches the page under an upper bound at a count; a reply serving nothing ends the range.</param>
    /// <param name="oldestOf">The oldest record time on a page, or null when the page has no usable times.</param>
    /// <param name="admittedThrough">The latest record time the source admits under a bound.</param>
    /// <param name="logger">Receives the warnings naming the collection and the time the crawl could not pass.</param>
    /// <param name="source">What is crawling, for logging.</param>
    /// <param name="collection">The collection paged, for logging.</param>
    /// <returns>Each served page's records not yielded before, possibly none.</returns>
    public static async IAsyncEnumerable<T[]> PageAsync<T>(
        DateTime? from,
        DateTime? to,
        int pageSize,
        int widestPageSize,
        Func<DateTime?, int, Task<TimePage<T>>> fetchPage,
        Func<T[], DateTime?> oldestOf,
        Func<DateTime, DateTime> admittedThrough,
        ILogger logger,
        string source,
        string collection)
        where T : ProcessableDocumentBase
    {
        var currentTo = to;
        var count = pageSize;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var (page, served) = await fetchPage(currentTo, count);

            if (served == 0)
                yield break;

            yield return page.Where(r => r.Id is not { Length: > 0 } id || seen.Add(id)).ToArray();

            var widened = count > pageSize;
            if (served < count && !widened)
                yield break;

            var oldestDate = oldestOf(page);
            if (!oldestDate.HasValue)
                yield break;

            if (currentTo is { } bound && oldestDate.Value > admittedThrough(bound))
            {
                logger.LogWarning(
                    "[{Source}] The oldest {Collection} record on a page, {Oldest:o}, is newer than the page's bound {Bound:o}; the source orders these records differently, so the crawl stops here",
                    source, collection, oldestDate.Value, currentTo);
                yield break;
            }

            if (currentTo is null || oldestDate.Value < currentTo.Value)
            {
                if (currentTo is null || SecondOf(oldestDate.Value) < SecondOf(currentTo.Value))
                    seen = page.Where(r => r.Id is { Length: > 0 }).Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);

                currentTo = oldestDate.Value;
                count = pageSize;
            }
            else if (served >= count && count < widestPageSize)
            {
                count = Math.Min(count * 2, widestPageSize);
                continue;
            }
            else
            {
                logger.LogWarning(
                    "[{Source}] {Returned} {Collection} records at {At:o} came back for {Count} asked; any more at that millisecond are skipped",
                    source, served, collection, currentTo.Value, count);
                currentTo = currentTo.Value.AddMilliseconds(-1);
                count = pageSize;
            }

            if (from.HasValue && currentTo < from)
                yield break;

            logger.LogDebug(
                "[{Source}] Paginating {Collection}, next page up to {Before:yyyy-MM-dd HH:mm:ss.fff}",
                source,
                collection,
                currentTo);
        }
    }

    /// <summary>
    ///     A <c>find[created_at][$lte]</c> value for a Nightscout source, which compares created_at as
    ///     a string. It writes it at millisecond precision ("...:00.000Z"), and legacy documents at
    ///     whole seconds ("...:00Z"), which sorts above every millisecond of that second. The bound is
    ///     written at millisecond precision so a record at the bound's own millisecond compares equal
    ///     and is included, and at whole seconds when it falls on one, so both spellings of that
    ///     instant are. Records it serves again above the instant are yielded once by
    ///     <see cref="PageAsync{T}"/>.
    /// </summary>
    public static string CreatedAtUpperBound(DateTime bound)
    {
        var upper = bound.ToUniversalTime();
        var format = IsWholeSecond(upper) ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        return upper.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>The latest created_at the bound <see cref="CreatedAtUpperBound"/> writes admits.</summary>
    public static DateTime CreatedAtAdmittedThrough(DateTime bound) =>
        IsWholeSecond(bound)
            ? SecondOf(bound).AddSeconds(1).AddTicks(-1)
            : MillisecondOf(bound).AddMilliseconds(1).AddTicks(-1);

    private static bool IsWholeSecond(DateTime at) => at.Millisecond == 0;

    private static DateTime SecondOf(DateTime at) => at.AddTicks(-(at.Ticks % TimeSpan.TicksPerSecond));

    private static DateTime MillisecondOf(DateTime at) => at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMillisecond));
}
