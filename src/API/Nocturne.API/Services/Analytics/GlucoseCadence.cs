using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// How much time each glucose reading stands for, read off the spacing of the readings
/// themselves.
/// </summary>
internal static class GlucoseCadence
{
    /// <summary>
    /// The cadence assumed where nothing publishes one: a series showing no interval of its own,
    /// or a device the catalogue carries no <see cref="CgmDeviceWindow.CadenceMinutes"/> for.
    /// </summary>
    internal const double DefaultCadenceMinutes = 5;

    /// <summary>
    /// How many intervals either side of a reading its local cadence is taken over.
    /// </summary>
    private const int LocalCadenceRadius = 3;

    /// <summary>
    /// The slowest cadence any CGM reports at: fifteen-minute history. No local cadence is taken
    /// to be slower, so an interval longer than twice this is always a gap.
    /// </summary>
    private const double MaxCadenceMinutes = 15;

    /// <summary>
    /// The longest interval that counts as evidence of a cadence: the slowest cadence plus half
    /// of it again for jitter. A longer interval is a dropout, and dropouts next to each other
    /// must not set the cadence that would excuse them.
    /// </summary>
    private const double MaxCadenceEvidenceMinutes = MaxCadenceMinutes * 1.5;

    /// <summary>
    /// The minutes from each reading to the next, in series order; empty for a series of one.
    /// </summary>
    internal static double[] ReadingIntervals(IList<SensorGlucose> sortedEntries)
    {
        var intervals = new double[Math.Max(sortedEntries.Count - 1, 0)];
        for (var i = 1; i < sortedEntries.Count; i++)
            intervals[i - 1] = (sortedEntries[i].Mills - sortedEntries[i - 1].Mills) / 60000.0;

        return intervals;
    }

    /// <summary>
    /// The cadence the series reports about itself: the median of its intervals, which a sensor
    /// outage or a warmup cannot drag the way it would a mean. Intervals of no elapsed time are
    /// excluded, and a series with none left falls back to <see cref="DefaultCadenceMinutes"/>.
    /// </summary>
    internal static double SeriesCadenceMinutes(IEnumerable<double> intervals)
    {
        var elapsed = intervals.Where(interval => interval > 0).Order().ToList();
        return elapsed.Count == 0 ? DefaultCadenceMinutes : GlucoseStatistics.Median(elapsed);
    }

    /// <summary>
    /// The minutes each reading of a time-ordered series stands for, and whether a gap follows it.
    /// A reading stands for the interval to the next one, up to twice the cadence of the intervals
    /// around it, unless that interval is a gap: longer than both
    /// <see cref="GlucoseEpisodeDetector.EpisodeMinutes"/> and twice that cadence. A reading before
    /// a gap, like the final reading, stands for one cadence, so time the sensor did not cover is
    /// credited to no zone. The cap makes the credit continuous across the gap boundary: a
    /// five-minute sensor missing two readings credits the reading before them at most ten
    /// minutes whether the interval is a hair under fifteen or a hair over.
    /// <para>
    /// The cadence is local rather than the series median because one window can hold several —
    /// a switch from a one-minute sensor to a five-minute one, or fifteen-minute history among a
    /// device's live readings — and a window-wide median would credit each slower reading a
    /// fraction of the time it stands for, or treat every one of its intervals as a gap. The
    /// fifteen-minute floor keeps any interval an episode could not have been missed in from
    /// counting as a gap, whatever the cadence.
    /// </para>
    /// </summary>
    internal static (double[] Minutes, bool[] GapAfter) ReadingMinutes(IList<SensorGlucose> sortedEntries)
    {
        var minutes = new double[sortedEntries.Count];
        var gapAfter = new bool[sortedEntries.Count];
        var intervals = ReadingIntervals(sortedEntries);

        for (var i = 0; i < sortedEntries.Count; i++)
        {
            var cadence = LocalCadenceMinutes(intervals, i);
            if (i < intervals.Length
                && intervals[i] <= Math.Max(GlucoseEpisodeDetector.EpisodeMinutes, cadence * 2))
            {
                minutes[i] = Math.Min(intervals[i], cadence * 2);
            }
            else
            {
                minutes[i] = cadence;
                gapAfter[i] = i < intervals.Length;
            }
        }

        return (minutes, gapAfter);
    }

    /// <summary>
    /// The median of the intervals within <see cref="LocalCadenceRadius"/> of the interval following
    /// reading <paramref name="index"/> that are evidence of a cadence (elapsed, and no longer than
    /// <see cref="MaxCadenceEvidenceMinutes"/>), capped at <see cref="MaxCadenceMinutes"/>. The
    /// interval itself is excluded so a gap does not vouch for itself. With no evidence nearby,
    /// <see cref="DefaultCadenceMinutes"/>.
    /// </summary>
    private static double LocalCadenceMinutes(double[] intervals, int index)
    {
        var around = new List<double>(LocalCadenceRadius * 2);
        for (var j = index - LocalCadenceRadius; j <= index + LocalCadenceRadius; j++)
        {
            if (j != index && j >= 0 && j < intervals.Length
                && intervals[j] > 0 && intervals[j] <= MaxCadenceEvidenceMinutes)
            {
                around.Add(intervals[j]);
            }
        }

        if (around.Count == 0)
            return DefaultCadenceMinutes;

        around.Sort();
        return Math.Min(GlucoseStatistics.Median(around), MaxCadenceMinutes);
    }
}
