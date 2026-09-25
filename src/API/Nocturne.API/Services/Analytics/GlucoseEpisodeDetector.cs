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
    /// order, in the order they began. Readings of one instant are taken as the one reading
    /// <see cref="GlucoseCadence.Instants"/> chooses for it.
    /// </summary>
    internal static List<GlucoseEpisode> Detect(
        IList<SensorGlucose> sortedEntries,
        GlycemicThresholds thresholds)
    {
        var zones = GlucoseStatistics.ExcludingZones(thresholds);
        var readings = GlucoseCadence.Instants(sortedEntries, thresholds);
        var (minutes, gapAfter) = GlucoseCadence.ReadingMinutes(readings);
        var episodes = new List<GlucoseEpisode>();

        // The run of readings on one side of the range, and within it the run beyond that side's
        // level 2 threshold; both reset on a gap.
        int runSide = 0, runStart = 0, runExtreme = 0, severeSide = 0;
        double runMinutes = 0, severeMinutes = 0;

        int episodeSide = 0, episodeStart = 0, lastBeyond = 0, extreme = 0;
        var severe = false;
        double returnMinutes = 0;

        for (var i = 0; i < readings.Count; i++)
        {
            var zone = (ExcludingZone)zones.Classify(readings[i].Mgdl);
            var side = zone switch
            {
                ExcludingZone.VeryLow or ExcludingZone.Low => -1,
                ExcludingZone.VeryHigh or ExcludingZone.High => 1,
                _ => 0,
            };
            var isSevere = zone is ExcludingZone.VeryLow or ExcludingZone.VeryHigh;

            if (side != runSide)
            {
                runSide = side;
                runStart = i;
                runExtreme = i;
                runMinutes = 0;
            }
            else if (MoreExtreme(side, i, runExtreme))
            {
                runExtreme = i;
            }
            runMinutes += minutes[i];

            if (isSevere && severeSide == side)
            {
                severeMinutes += minutes[i];
            }
            else
            {
                severeSide = isSevere ? side : 0;
                severeMinutes = isSevere ? minutes[i] : 0;
            }

            if (episodeSide != 0)
            {
                if (side == episodeSide)
                {
                    returnMinutes = 0;
                    lastBeyond = i;
                    if (MoreExtreme(side, i, extreme))
                        extreme = i;
                }
                else if ((returnMinutes += minutes[i]) >= EpisodeMinutes)
                {
                    Close();
                }
            }

            if (episodeSide == 0 && runSide != 0 && runMinutes >= EpisodeMinutes)
            {
                episodeSide = runSide;
                episodeStart = runStart;
                lastBeyond = i;
                extreme = runExtreme;
                severe = false;
                returnMinutes = 0;
            }

            if (episodeSide != 0 && severeSide == episodeSide && severeMinutes >= EpisodeMinutes)
                severe = true;

            if (gapAfter[i])
            {
                Close();
                runSide = 0;
                severeSide = 0;
                severeMinutes = 0;
            }
        }

        Close();
        return episodes;

        bool MoreExtreme(int side, int candidate, int current) =>
            side < 0
                ? readings[candidate].Mgdl < readings[current].Mgdl
                : readings[candidate].Mgdl > readings[current].Mgdl;

        void Close()
        {
            if (episodeSide == 0)
                return;

            var end = readings[lastBeyond];
            episodes.Add(new GlucoseEpisode(
                BelowRange: episodeSide < 0,
                Severe: severe,
                StartAt: readings[episodeStart].Timestamp,
                EndAt: end.Timestamp.AddMinutes(minutes[lastBeyond]),
                Extreme: readings[extreme]));
            episodeSide = 0;
        }
    }
}
