namespace Nocturne.API.Helpers;

/// <summary>
/// Pages a source, in the order its read asks for, through a find's in-memory conditions, for the
/// reads whose limit cannot be pushed past a filter only the projected document can answer. Both
/// modes start at four times the records needed, at least 100, and grow fourfold up to the budget.
/// </summary>
public static class FindFilteredFetch
{
    /// <summary>
    /// The matches among the first <paramref name="budget"/> records, read in pages by offset,
    /// stopping once <paramref name="needed"/> match or the source runs out. For a source whose pages
    /// are stable, so that no record is read twice.
    /// </summary>
    /// <param name="fetchPage">Reads <c>limit</c> records from <c>offset</c>, in the read's order.</param>
    /// <param name="matches">The find's test of one record.</param>
    /// <param name="key">
    /// A record's identity. A record written while the pages are read shifts the next page by one, so
    /// a record seen twice is kept once.
    /// </param>
    /// <param name="needed">The matches the caller can use: its page end, <c>count + skip</c>.</param>
    /// <param name="budget">The most records read in all.</param>
    /// <returns>
    /// The matches, in the source's order, and whether the budget ended the read with records left unread.
    /// </returns>
    public static async Task<(List<T> Matches, bool Truncated)> MatchAsync<T>(
        Func<int, int, Task<IReadOnlyList<T>>> fetchPage,
        Func<T, bool> matches,
        Func<T, string?> key,
        long needed,
        int budget)
    {
        var matched = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var read = 0;
        var pageSize = FirstPage(needed, budget);

        while (true)
        {
            var page = await fetchPage(pageSize, read);
            read += page.Count;
            matched.AddRange(page.Where(record => (key(record) is not { } id || seen.Add(id)) && matches(record)));

            if (matched.Count >= needed || page.Count < pageSize)
                return (matched, false);
            if (read >= budget)
                return (matched, true);

            pageSize = Grow(pageSize, budget - read);
        }
    }

    /// <summary>
    /// The matches in one read of the first records, re-read whole at each larger window until
    /// <paramref name="needed"/> match, the source runs out, or the window reaches
    /// <paramref name="budget"/>. For a source that pages by limit only, such as a merge of several
    /// tables, whose later windows need not extend its earlier ones record for record.
    /// </summary>
    /// <param name="fetchFirst">Reads the first <c>limit</c> records, in the read's order.</param>
    /// <param name="matches">The find's test of one record.</param>
    /// <param name="needed">The matches the caller can use: its page end, <c>count + skip</c>.</param>
    /// <param name="budget">The widest window read.</param>
    /// <returns>
    /// The matches of the last window, and whether the budget ended the read with records left unread.
    /// </returns>
    public static async Task<(List<T> Matches, bool Truncated)> MatchWindowAsync<T>(
        Func<int, Task<IReadOnlyList<T>>> fetchFirst,
        Func<T, bool> matches,
        long needed,
        int budget)
    {
        var window = FirstPage(needed, budget);

        while (true)
        {
            var records = await fetchFirst(window);
            var matched = records.Where(matches).ToList();
            if (matched.Count >= needed || records.Count < window || window >= budget)
                return (matched, matched.Count < needed && records.Count >= window);

            window = Grow(window, budget);
        }
    }

    private static int FirstPage(long needed, int budget) => (int)Math.Min(Math.Max(needed * 4, 100), budget);

    private static int Grow(int size, int limit) => (int)Math.Min((long)size * 4, limit);
}
