namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>How APS snapshots are told apart as loop cycles, for every page that reads them as such.</summary>
public static class LoopCycles
{
    /// <summary>
    /// One cycle per instant, oldest first: two uploaders, or one under two device names, can post
    /// the same cycle, and where only one of them saw it enacted, it was. Otherwise the first in
    /// <paramref name="cycles"/> order is kept.
    /// </summary>
    public static List<T> Distinct<T>(IEnumerable<T> cycles, Func<T, long> mills, Func<T, bool> enacted) =>
        [.. cycles.GroupBy(mills).OrderBy(g => g.Key).Select(g => g.Where(enacted).DefaultIfEmpty(g.First()).First())];
}
