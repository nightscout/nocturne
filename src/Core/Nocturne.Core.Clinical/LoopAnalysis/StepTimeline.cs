namespace Nocturne.Core.Clinical.LoopAnalysis;

public static class SortedSearch
{
    /// <summary>
    /// Index of the first element of <paramref name="sorted"/> whose <paramref name="end"/> is after
    /// <paramref name="mills"/>, or its count when none is. The elements must be ordered so that
    /// <paramref name="end"/> never decreases.
    /// </summary>
    public static int FirstEndingAfter<T>(IReadOnlyList<T> sorted, long mills, Func<T, long> end)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (end(sorted[mid]) <= mills) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>A value held over <c>[Start, End)</c>.</summary>
public readonly record struct Step<T>(long Start, long End, T Value);

/// <summary>Sorted, non-overlapping steps of a value over time, looked up by binary search.</summary>
public sealed class StepTimeline<T>
{
    private readonly Step<T>[] _steps;

    private StepTimeline(Step<T>[] steps) => _steps = steps;

    public IReadOnlyList<Step<T>> Steps => _steps;

    /// <summary>
    /// Flattens possibly overlapping steps: wherever several cover an instant, the one that started
    /// last is in force, as a later override or temporary target replaces an earlier one.
    /// </summary>
    public static StepTimeline<T> LatestStartedWins(IEnumerable<Step<T>> steps)
    {
        var sorted = steps.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToArray();
        var bounds = sorted.SelectMany(s => new[] { s.Start, s.End }).Distinct().Order().ToArray();
        var active = new PriorityQueue<int, (long Start, int Index)>(
            Comparer<(long Start, int Index)>.Create((a, b) => b.CompareTo(a)));
        var result = new List<Step<T>>();
        var next = 0;
        for (var k = 0; k + 1 < bounds.Length; k++)
        {
            var at = bounds[k];
            while (next < sorted.Length && sorted[next].Start <= at)
            {
                active.Enqueue(next, (sorted[next].Start, next));
                next++;
            }
            while (active.TryPeek(out var top, out _) && sorted[top].End <= at)
                active.Dequeue();
            if (!active.TryPeek(out var winner, out _))
                continue;
            var value = sorted[winner].Value;
            if (result.Count > 0 && result[^1].End == at && EqualityComparer<T>.Default.Equals(result[^1].Value, value))
                result[^1] = result[^1] with { End = bounds[k + 1] };
            else
                result.Add(new Step<T>(at, bounds[k + 1], value));
        }
        return new StepTimeline<T>([.. result]);
    }

    /// <summary>Index of the first step ending after <paramref name="mills"/>.</summary>
    public int FirstEndingAfter(long mills) => SortedSearch.FirstEndingAfter(_steps, mills, static s => s.End);

    public bool TryAt(long mills, out T value)
    {
        var i = FirstEndingAfter(mills);
        if (i < _steps.Length && _steps[i].Start <= mills)
        {
            value = _steps[i].Value;
            return true;
        }
        value = default!;
        return false;
    }
}
