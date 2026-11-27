using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The recent readings the windowed conditions read (<see cref="SensorContext.GlucoseSeries"/>,
/// docs/alerts/engine-semantics.md §4): every reading with a glucose value of the newest reading's
/// stream (<see cref="CanonicalGlucoseStream.StreamKey"/>) and uploader, from <see cref="Span"/> before
/// the newest reading up to and including it. Live (<see cref="SensorContextEnricher"/>) and replay
/// (<see cref="AlertReplayService"/>) each select from the canonical readings they load by this one rule.
/// </summary>
/// <remarks>
/// Stream and uploader match as <see cref="ReadingTrend"/> pairs a reading with the one before it: the
/// registered device, or with none the device, and the data source, a null and an empty one being the
/// same. The upper bound is the newest reading, not the evaluation instant, so a reading dated beyond the
/// clock tolerance, which the context does not select, is never in the series.
/// </remarks>
internal static class RecentReadings
{
    /// <summary>The longest window a condition names, which bounds the readings sent.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(180);

    /// <summary>
    /// The series of the newest reading, from <paramref name="source"/> at <paramref name="newestAt"/>,
    /// among <paramref name="readings"/> in any order; oldest first.
    /// </summary>
    public static IReadOnlyList<GlucoseSeriesPoint> Of(
        ReadingSource source, DateTime newestAt, IEnumerable<SensorGlucose> readings)
    {
        var from = newestAt - Span;
        var dataSource = source.DataSource ?? "";
        var device = source.Device ?? "";
        return readings
            .Where(r => AlertReadings.HasValue(r)
                        && r.Timestamp >= from
                        && r.Timestamp <= newestAt
                        && (r.DataSource ?? "") == dataSource
                        && r.PatientDeviceId == source.PatientDeviceId
                        && (source.PatientDeviceId is not null || (r.Device ?? "") == device))
            .OrderBy(r => r.Timestamp)
            .Select(r => new GlucoseSeriesPoint(r.Timestamp, r.Mgdl.ToDecimal()))
            .ToList();
    }
}
