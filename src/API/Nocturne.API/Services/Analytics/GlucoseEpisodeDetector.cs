using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// One glucose episode, as <see cref="TimeInRangeEpisodes"/> defines it.
/// </summary>
/// <param name="BelowRange">A low episode rather than a high one.</param>
/// <param name="Severe">The episode reached level 2: very low, or very high.</param>
/// <param name="StartAt">The first reading beyond the threshold.</param>
/// <param name="EndAt">
/// When the last reading beyond the threshold stops standing for the glucose, as
/// <see cref="GlucoseCadence.ReadingMinutes"/> credits it.
/// </param>
/// <param name="Extreme">The lowest reading of a low episode, or the highest of a high one.</param>
internal sealed record GlucoseEpisode(
    bool BelowRange,
    bool Severe,
    DateTime StartAt,
    DateTime EndAt,
    SensorGlucose Extreme)
{
    /// <summary>
    /// The minutes from <see cref="StartAt"/> to <see cref="EndAt"/>. A gap ends an episode, so
    /// readings cover all of them.
    /// </summary>
    public double DurationMinutes => (EndAt - StartAt).TotalMinutes;
}

/// <summary>
/// Finds the episodes in a series of readings. Every report that counts or lists glucose episodes
/// takes them from here, so that each finds the same episodes with the same durations.
/// </summary>
internal static class GlucoseEpisodeDetector
{
    /// <summary>
    /// The consensus event duration <see cref="TimeInRangeEpisodes"/> is defined by.
    /// </summary>
    internal const double EpisodeMinutes = 15;

    /// <summary>
    /// The episodes in <paramref name="sortedEntries"/>, which must be plausible readings in time
    /// order, in the order they began.
    /// <para>
    /// Readings stamped at the same instant are taken together, as one reading standing for the
    /// minutes <see cref="GlucoseCadence.ReadingMinutes"/> credits the last of them. If they
    /// disagree, the instant stays on the side the open episode or run is on when any of them
    /// is, so the order two sources' readings arrive in cannot break a run.
    /// </para>
    /// </summary>
    internal static List<GlucoseEpisode> Detect(
        IList<SensorGlucose> sortedEntries,
        GlycemicThresholds thresholds)
    {
        var zones = GlucoseStatistics.ExcludingZones(thresholds);
        var (minutes, gapAfter) = GlucoseCadence.ReadingMinutes(sortedEntries);
        var episodes = new List<GlucoseEpisode>();

        // The run of instants on one side of the range, and within it the run beyond that side's
        // level 2 threshold; both reset on a gap.
        int runSide = 0, runStart = 0, runExtreme = 0, severeSide = 0;
        double runMinutes = 0, severeMinutes = 0;

        int episodeSide = 0, episodeStart = 0, lastBeyond = 0, extreme = 0;
        var severe = false;
        double returnMinutes = 0;

        for (var first = 0; first < sortedEntries.Count;)
        {
            var last = first;
            while (last + 1 < sortedEntries.Count && sortedEntries[last + 1].Mills == sortedEntries[first].Mills)
                last++;

            var side = InstantSide(first, last);
            var reading = MostExtreme(side, first, last);
            var isSevere = side != 0 && IsSevere(first, last, side);
            var credit = minutes[last];

            if (side != runSide)
            {
                runSide = side;
                runStart = first;
                runExtreme = reading;
                runMinutes = 0;
            }
            else if (MoreExtreme(side, reading, runExtreme))
            {
                runExtreme = reading;
            }
            runMinutes += credit;

            if (isSevere && severeSide == side)
            {
                severeMinutes += credit;
            }
            else
            {
                severeSide = isSevere ? side : 0;
                severeMinutes = isSevere ? credit : 0;
            }

            if (episodeSide != 0)
            {
                if (side == episodeSide)
                {
                    returnMinutes = 0;
                    lastBeyond = last;
                    if (MoreExtreme(side, reading, extreme))
                        extreme = reading;
                }
                else if ((returnMinutes += credit) >= EpisodeMinutes)
                {
                    Close();
                }
            }

            if (episodeSide == 0 && runSide != 0 && runMinutes >= EpisodeMinutes)
            {
                episodeSide = runSide;
                episodeStart = runStart;
                lastBeyond = last;
                extreme = runExtreme;
                severe = false;
                returnMinutes = 0;
            }

            if (episodeSide != 0 && severeSide == episodeSide && severeMinutes >= EpisodeMinutes)
                severe = true;

            if (gapAfter[last])
            {
                Close();
                runSide = 0;
                severeSide = 0;
                severeMinutes = 0;
            }

            first = last + 1;
        }

        Close();
        return episodes;

        int Side(int index) =>
            (ExcludingZone)zones.Classify(sortedEntries[index].Mgdl) switch
            {
                ExcludingZone.VeryLow or ExcludingZone.Low => -1,
                ExcludingZone.VeryHigh or ExcludingZone.High => 1,
                _ => 0,
            };

        int InstantSide(int from, int to)
        {
            foreach (var held in (ReadOnlySpan<int>)[episodeSide, runSide])
            {
                if (held == 0)
                    continue;
                for (var j = from; j <= to; j++)
                {
                    if (Side(j) == held)
                        return held;
                }
            }

            return Side(to);
        }

        bool IsSevere(int from, int to, int side)
        {
            for (var j = from; j <= to; j++)
            {
                if (Side(j) == side
                    && (ExcludingZone)zones.Classify(sortedEntries[j].Mgdl)
                        is ExcludingZone.VeryLow or ExcludingZone.VeryHigh)
                {
                    return true;
                }
            }

            return false;
        }

        int MostExtreme(int side, int from, int to)
        {
            var most = to;
            for (var j = from; j <= to; j++)
            {
                if (Side(j) == side && (Side(most) != side || MoreExtreme(side, j, most)))
                    most = j;
            }

            return most;
        }

        bool MoreExtreme(int side, int candidate, int current) =>
            side < 0
                ? sortedEntries[candidate].Mgdl < sortedEntries[current].Mgdl
                : sortedEntries[candidate].Mgdl > sortedEntries[current].Mgdl;

        void Close()
        {
            if (episodeSide == 0)
                return;

            var end = sortedEntries[lastBeyond];
            episodes.Add(new GlucoseEpisode(
                BelowRange: episodeSide < 0,
                Severe: severe,
                StartAt: sortedEntries[episodeStart].Timestamp,
                EndAt: end.Timestamp.AddMinutes(minutes[lastBeyond]),
                Extreme: sortedEntries[extreme]));
            episodeSide = 0;
        }
    }
}
