namespace Nocturne.Core.Clinical.Statistics;

/// <summary>
/// The union of half-open time intervals <c>[Start, End)</c>. A "% of time" figure credits the
/// time its records cover, not the sum of their lengths: two records over the same minutes, such
/// as one reading posted by two uploaders or two overlapping temp basals, cover those minutes once.
/// </summary>
public static class IntervalUnion
{
    /// <summary>
    /// The disjoint intervals covering exactly the time some interval covers, in time order.
    /// Intervals that touch join, and empty or reversed intervals cover nothing.
    /// </summary>
    public static List<(DateTime Start, DateTime End)> Merge(
        IEnumerable<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
                merged.Add(interval);
            else if (interval.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, interval.End);
        }

        return merged;
    }

    /// <summary>The total length of the union of <paramref name="intervals"/>.</summary>
    public static TimeSpan Length(IEnumerable<(DateTime Start, DateTime End)> intervals) =>
        Merge(intervals).Aggregate(TimeSpan.Zero, (total, i) => total + (i.End - i.Start));
}
