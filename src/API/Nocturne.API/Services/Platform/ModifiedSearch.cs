using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// A v3 search by modification stamp, served by walking a store's modified-since pages, which are
/// index-served, oldest stamp first, and end on a millisecond boundary so resuming at a page's
/// stamp loses nothing.
/// </summary>
internal static class ModifiedSearch
{
    internal const int PageSize = 500;

    /// <summary>Most records one search reads before it stops, as the find-filtered reads cap their window.</summary>
    internal const int MaxRead = 100_000;

    /// <summary>
    /// The stamp a search resumes after when it is served by modified-since: the find's own
    /// <c>srvModified</c> bound, or the start of history for an unfiltered ascending <c>sort=srvModified</c>.
    /// Null when the find or sort needs the time-ordered read.
    /// </summary>
    internal static long? CursorFor(FindQuery find, ReadSortField sort, bool descending) =>
        find.ModifiedAfterMills
        ?? (sort == ReadSortField.SrvModified && !descending && find.IsEmpty ? 0L : null);

    /// <summary>
    /// Whether the page is cut by stamp, so reading can stop once it is full: only an ascending
    /// <c>srvModified</c> order is the order the pages arrive in.
    /// </summary>
    internal static bool ArrivesInOrder(ReadSortField sort, bool descending) =>
        sort == ReadSortField.SrvModified && !descending;

    /// <summary>One modified-since page: its records, the stamp to resume after, and whether it was the last.</summary>
    internal sealed record Page<T>(IReadOnlyList<T> Records, long? NextCursor, bool Last);

    /// <summary>
    /// Reads live records stamped after <paramref name="cursor"/>, oldest stamp first, until
    /// <paramref name="needed"/> are held, or the store is exhausted when <paramref name="needed"/> is
    /// null, or <paramref name="maxRead"/> records have been read.
    /// </summary>
    internal static async Task<List<T>> CollectAsync<T>(
        Func<long, int, CancellationToken, Task<Page<T>>> fetch,
        Func<T, bool> isDeleted,
        long cursor,
        long? needed,
        int maxRead,
        CancellationToken ct)
    {
        var live = new List<T>();
        var read = 0;

        while (read < maxRead && (needed is null || live.Count < needed))
        {
            var page = await fetch(cursor, PageSize, ct);
            read += page.Records.Count;
            live.AddRange(page.Records.Where(r => !isDeleted(r)));

            if (page.Last || page.NextCursor is not { } next || next <= cursor)
                break;

            cursor = next;
        }

        return live;
    }
}
