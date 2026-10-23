namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>A half-open <c>[Start, End)</c> span of Unix milliseconds.</summary>
public readonly record struct MillsInterval(long Start, long End)
{
    public long Length => End - Start;
}

/// <summary>
/// An immutable set of instants held as sorted, disjoint, non-adjacent half-open intervals.
/// </summary>
public sealed class IntervalSet
{
    private readonly MillsInterval[] _intervals;

    private IntervalSet(MillsInterval[] normalised) => _intervals = normalised;

    public static IntervalSet Empty { get; } = new([]);

    public static IntervalSet Of(long start, long end) =>
        end > start ? new IntervalSet([new MillsInterval(start, end)]) : Empty;

    /// <summary>The union of <paramref name="intervals"/>; empty and inverted ones are dropped.</summary>
    public static IntervalSet Of(IEnumerable<MillsInterval> intervals)
    {
        var sorted = intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ToList();
        var merged = new List<MillsInterval>(sorted.Count);
        foreach (var interval in sorted)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, interval.End) };
            else
                merged.Add(interval);
        }
        return new IntervalSet([.. merged]);
    }

    public IReadOnlyList<MillsInterval> Intervals => _intervals;

    public bool IsEmpty => _intervals.Length == 0;

    public long TotalMs => _intervals.Sum(i => i.Length);

    public bool Contains(long mills)
    {
        var index = FirstEndingAfter(mills);
        return index < _intervals.Length && _intervals[index].Start <= mills;
    }

    /// <summary>Whether any instant of <c>[start, end)</c> is in the set.</summary>
    public bool Overlaps(long start, long end)
    {
        var index = FirstEndingAfter(start);
        return end > start && index < _intervals.Length && _intervals[index].Start < end;
    }

    /// <summary>Whether every instant of <c>[start, end)</c> is in the set.</summary>
    public bool Covers(long start, long end)
    {
        var index = FirstEndingAfter(start);
        return index < _intervals.Length && _intervals[index].Start <= start && _intervals[index].End >= end;
    }

    /// <summary>Whether an interval of the set starts in <c>(after, upTo]</c>.</summary>
    public bool StartsWithin(long after, long upTo)
    {
        var index = FirstEndingAfter(after);
        if (index < _intervals.Length && _intervals[index].Start <= after)
            index++;
        return index < _intervals.Length && _intervals[index].Start <= upTo;
    }

    public IntervalSet Union(IntervalSet other) => Of(_intervals.Concat(other._intervals));

    public IntervalSet Intersect(IntervalSet other)
    {
        var result = new List<MillsInterval>();
        int i = 0, j = 0;
        while (i < _intervals.Length && j < other._intervals.Length)
        {
            var start = Math.Max(_intervals[i].Start, other._intervals[j].Start);
            var end = Math.Min(_intervals[i].End, other._intervals[j].End);
            if (end > start)
                result.Add(new MillsInterval(start, end));
            if (_intervals[i].End < other._intervals[j].End)
                i++;
            else
                j++;
        }
        return new IntervalSet([.. result]);
    }

    public IntervalSet Subtract(IntervalSet other)
    {
        var result = new List<MillsInterval>();
        var j = 0;
        foreach (var interval in _intervals)
        {
            var cursor = interval.Start;
            while (j < other._intervals.Length && other._intervals[j].End <= cursor)
                j++;
            for (var k = j; k < other._intervals.Length && other._intervals[k].Start < interval.End; k++)
            {
                if (other._intervals[k].Start > cursor)
                    result.Add(new MillsInterval(cursor, other._intervals[k].Start));
                cursor = Math.Max(cursor, other._intervals[k].End);
            }
            if (cursor < interval.End)
                result.Add(new MillsInterval(cursor, interval.End));
        }
        return new IntervalSet([.. result]);
    }

    /// <summary>Hands each piece of <c>[start, end)</c> inside the set to <paramref name="piece"/>, in order.</summary>
    public void ForEachOverlap(long start, long end, Action<long, long> piece)
    {
        for (var i = FirstEndingAfter(start); i < _intervals.Length && _intervals[i].Start < end; i++)
        {
            var pieceStart = Math.Max(start, _intervals[i].Start);
            var pieceEnd = Math.Min(end, _intervals[i].End);
            if (pieceEnd > pieceStart)
                piece(pieceStart, pieceEnd);
        }
    }

    /// <summary>Hands each piece of <c>[start, end)</c> outside the set to <paramref name="piece"/>, in order.</summary>
    public void ForEachGap(long start, long end, Action<long, long> piece)
    {
        var cursor = start;
        for (var i = FirstEndingAfter(start); i < _intervals.Length && _intervals[i].Start < end; i++)
        {
            if (_intervals[i].Start > cursor)
                piece(cursor, _intervals[i].Start);
            cursor = Math.Max(cursor, _intervals[i].End);
        }
        if (cursor < end)
            piece(cursor, end);
    }

    private int FirstEndingAfter(long mills) => SortedSearch.FirstEndingAfter(_intervals, mills, static i => i.End);
}
