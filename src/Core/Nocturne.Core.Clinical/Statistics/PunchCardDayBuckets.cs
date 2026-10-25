using Nocturne.Core.Models;

namespace Nocturne.Core.Clinical.Statistics;

/// <summary>
/// Inclusive millisecond bounds of one tenant-local calendar day. The end is one tick before the
/// next local midnight, floored to a millisecond, so a record at exactly local midnight belongs to
/// the day it opens.
/// </summary>
public readonly record struct PunchCardDayWindow(long StartMs, long EndMs)
{
    public static PunchCardDayWindow For(DateTime localDay, TimeZoneInfo tz)
    {
        var startLocal = DateTime.SpecifyKind(localDay.Date, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1).AddTicks(-1);
        return new PunchCardDayWindow(
            TimeZoneInfo.ConvertTimeToUtc(startLocal, tz).ToMills(),
            TimeZoneInfo.ConvertTimeToUtc(endLocal, tz).ToMills());
    }
}

public static class PunchCardDayBuckets
{
    /// <summary>
    /// Groups <paramref name="records"/> by every window holding their mills in one pass over the
    /// records. A zone that skips local midnight makes neighbouring windows overlap, and a record
    /// in the overlap belongs to both days, as it would filtering each day on its own. Windows may
    /// be in any order. Records keep their input order within a bucket.
    /// </summary>
    public static IReadOnlyList<T>[] Bucket<T>(
        IEnumerable<T> records,
        IReadOnlyList<PunchCardDayWindow> windows,
        Func<T, long> mills)
    {
        var order = Enumerable.Range(0, windows.Count).OrderBy(i => windows[i].StartMs).ToArray();
        var starts = new long[order.Length];
        var maxEndUpTo = new long[order.Length];
        for (var j = 0; j < order.Length; j++)
        {
            starts[j] = windows[order[j]].StartMs;
            maxEndUpTo[j] = j == 0
                ? windows[order[j]].EndMs
                : Math.Max(maxEndUpTo[j - 1], windows[order[j]].EndMs);
        }

        var buckets = new List<T>?[windows.Count];
        foreach (var record in records)
        {
            var ms = mills(record);
            for (var j = LastStartAtOrBefore(starts, ms); j >= 0 && maxEndUpTo[j] >= ms; j--)
            {
                var day = order[j];
                if (ms <= windows[day].EndMs) (buckets[day] ??= []).Add(record);
            }
        }

        var result = new IReadOnlyList<T>[buckets.Length];
        for (var i = 0; i < buckets.Length; i++) result[i] = buckets[i] ?? [];
        return result;
    }

    private static int LastStartAtOrBefore(long[] starts, long ms)
    {
        int lo = 0, hi = starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (starts[mid] <= ms)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}
