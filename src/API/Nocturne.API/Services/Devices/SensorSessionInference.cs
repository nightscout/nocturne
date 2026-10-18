using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>A sensor session start read off a warm-up gap in one CGM's readings.</summary>
/// <param name="StartedAt">When the new sensor was inserted: one warm-up before readings resumed.</param>
/// <param name="LastReadingBefore">The old sensor's last reading.</param>
/// <param name="FirstReadingAfter">The new sensor's first reading.</param>
internal readonly record struct InferredSensorStart(DateTime StartedAt, DateTime LastReadingBefore, DateTime FirstReadingAfter);

/// <summary>
/// Finds sensor changes in one CGM's readings from the silence a new sensor keeps while it warms
/// up. A gap at least <see cref="CgmProperties.WarmupMinutes"/> long, and not so long that it is an
/// outage, is taken as a change. For short warm-ups that admits some dropouts, which is accepted:
/// the series must be one device's own readings, since a second CGM worn across the change fills
/// the gap.
/// </summary>
internal static class SensorSessionInference
{
    /// <summary>
    /// The longest a sensor change is taken to add to the warm-up: removing the old sensor and
    /// inserting the new one. A longer gap is an outage, or a break between sensors.
    /// </summary>
    public static readonly TimeSpan MaxChangeTime = TimeSpan.FromHours(6);

    /// <summary>
    /// How long readings must have resumed before a gap is judged. A phone that reconnects after a
    /// dropout backfills the missed readings within minutes of the first live one, and the gap
    /// closes.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromHours(1);

    /// <summary>
    /// How far either side of a gap a recorded session event still accounts for it, since a change
    /// is often logged before the old sensor comes off or once the new one is warm.
    /// </summary>
    public static readonly TimeSpan RecordedEventSlack = TimeSpan.FromHours(2);

    /// <summary>The device events that mark a new sensor session.</summary>
    public static readonly IReadOnlyCollection<DeviceEventType> SessionEventTypes =
        [DeviceEventType.SensorStart, DeviceEventType.SensorChange, DeviceEventType.TransmitterSensorInsert];

    /// <summary>
    /// How far apart two records of one sensor change can land: deduplication's wide matching window,
    /// and the spread of an uploader logging a sensor change and a sensor start for the same insertion.
    /// </summary>
    public static readonly TimeSpan CopySpread = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The sensor sessions <paramref name="events"/> record, oldest first: of a run of session events
    /// each within <see cref="CopySpread"/> of the one before, only the first.
    /// </summary>
    public static IReadOnlyList<DeviceEvent> DistinctSessions(IEnumerable<DeviceEvent> events)
    {
        var sessions = new List<DeviceEvent>();
        DateTime? previous = null;
        foreach (var e in events.Where(e => SessionEventTypes.Contains(e.EventType)).OrderBy(e => e.Timestamp))
        {
            if (previous is null || e.Timestamp - previous > CopySpread)
                sessions.Add(e);
            previous = e.Timestamp;
        }
        return sessions;
    }

    /// <param name="readings">Timestamps of one CGM's readings, in any order.</param>
    /// <param name="cgm">The CGM's catalog properties.</param>
    /// <param name="recordedSessionEvents">Timestamps of sensor starts and changes already recorded
    /// for the CGM by an uploader or the user. A gap one of these accounts for is not inferred.</param>
    /// <param name="now">The current UTC time.</param>
    public static IReadOnlyList<InferredSensorStart> Detect(
        IEnumerable<DateTime> readings,
        CgmProperties cgm,
        IEnumerable<DateTime> recordedSessionEvents,
        DateTime now)
    {
        var warmup = TimeSpan.FromMinutes(cgm.WarmupMinutes);
        var times = readings.Distinct().Order().ToList();
        var recorded = recordedSessionEvents.ToList();
        var starts = new List<InferredSensorStart>();

        for (var i = 1; i < times.Count; i++)
        {
            var before = times[i - 1];
            var after = times[i];
            var gap = after - before;

            if (gap < warmup || gap > warmup + MaxChangeTime || now - after < SettleTime)
                continue;

            if (recorded.Any(e => e >= before - RecordedEventSlack && e <= after + RecordedEventSlack))
                continue;

            starts.Add(new InferredSensorStart(after - warmup, before, after));
        }

        return starts;
    }
}
