using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The recent readings the windowed conditions read (<see cref="SensorContext.GlucoseSeries"/>,
/// docs/alerts/engine-semantics.md §4): every reading with a glucose value of the newest reading's
/// stream (<see cref="CanonicalGlucoseStream.StreamKey"/>) and uploader, from <see cref="Span"/> before
/// the newest reading up to and including it, chosen as <see cref="ReadingTrend"/> chooses a reading's
/// earlier one. Live (<see cref="SensorContextEnricher"/>) reads them through
/// <see cref="ICanonicalGlucoseService.GetStreamSeriesAsync"/> and replay
/// (<see cref="AlertReplayService"/>) selects them from the canonical and hidden readings it loads, both
/// by <see cref="Of"/>.
/// </summary>
/// <remarks>
/// A copy deduplication hides is in the series: with two apps uploading one CGM the primary of each
/// group is whichever copy came first, so an uploader's own copy is often the hidden one. A copy in the
/// newest reading's own duplicate group is not, being the same reading. The upper bound is the newest
/// reading, not the evaluation instant, so a reading dated beyond the clock tolerance, which the context
/// does not select, is never in the series.
/// </remarks>
internal static class RecentReadings
{
    /// <summary>The longest window a condition names, which bounds the readings sent.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(180);

    /// <summary>
    /// The newest reading of <paramref name="context"/>, carrying what the series read matches on, or
    /// null when the context does not name it.
    /// </summary>
    public static SensorGlucose? NewestOf(SensorContext context) =>
        context is { LatestSource: { ReadingId: { } id } source, LatestTimestamp: { } at }
            ? new SensorGlucose
            {
                Id = id,
                Timestamp = at,
                DataSource = source.DataSource,
                Device = source.Device,
                PatientDeviceId = source.PatientDeviceId,
            }
            : null;

    /// <summary>The series of <paramref name="newest"/>, read from the stored readings.</summary>
    public static async Task<IReadOnlyList<GlucoseSeriesPoint>> ReadAsync(
        SensorGlucose newest, ICanonicalGlucoseService canonical, CancellationToken ct)
    {
        var readings = await canonical.GetStreamSeriesAsync(newest, newest.Timestamp - Span, ct);
        return Of(newest, readings.Select(r => (r, (Guid?)null)));
    }

    /// <summary>
    /// The series of <paramref name="newest"/> among <paramref name="candidates"/>, readings of any
    /// stream in any order, each with the primary of its duplicate group when it is a non-primary copy;
    /// oldest first.
    /// </summary>
    public static IReadOnlyList<GlucoseSeriesPoint> Of(
        SensorGlucose newest, IEnumerable<(SensorGlucose Reading, Guid? DuplicateOf)> candidates)
    {
        var stream = CanonicalGlucoseStream.StreamKey(newest);
        var source = newest.DataSource ?? "";
        var from = newest.Timestamp - Span;
        return candidates
            .Where(c => c.Reading.Id == newest.Id || c.DuplicateOf != newest.Id)
            .Select(c => c.Reading)
            .Where(r => AlertReadings.HasValue(r)
                        && r.Timestamp >= from
                        && r.Timestamp <= newest.Timestamp
                        && (r.DataSource ?? "") == source
                        && CanonicalGlucoseStream.StreamKey(r) == stream)
            .DistinctBy(r => r.Id)
            .OrderBy(r => r.Timestamp)
            .Select(r => new GlucoseSeriesPoint(r.Timestamp, r.Mgdl.ToDecimal()))
            .ToList();
    }
}
