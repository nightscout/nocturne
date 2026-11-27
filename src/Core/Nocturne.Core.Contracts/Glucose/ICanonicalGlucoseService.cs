using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// The newest readings of the canonical glucose stream (<see cref="CanonicalGlucoseStream"/>), under
/// the demo-mode source filtering single-stream clients are shown.
/// </summary>
/// <remarks>
/// The canonical view is what single-stream consumers see: v1/v3 REST, the legacy realtime
/// broadcast, alarms, and unfiltered statistics. Multi-stream access is a V4-only concern.
/// </remarks>
/// <seealso cref="CanonicalGlucoseStream"/>
public interface ICanonicalGlucoseService
{
    /// <summary>
    /// The most recent canonical reading, however old, so delayed-sync sources (hours-late connector
    /// backfills) still surface a latest value. Null only when the tenant has no readings at all.
    /// Staleness judgement belongs to the caller, exactly as it did when consumers used the newest
    /// reading of a write batch.
    /// </summary>
    Task<SensorGlucose?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>
    /// The most recent canonical reading timestamped at or before <paramref name="notAfter"/>, however
    /// old; null when there is none.
    /// </summary>
    Task<SensorGlucose?> GetLatestAsync(DateTime notAfter, CancellationToken ct = default);

    /// <summary>
    /// Canonical readings timestamped at or after <paramref name="since"/>, newest first, under the
    /// same source filtering as <see cref="GetLatestAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> GetRecentAsync(DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Every canonical reading timestamped in [<paramref name="since"/>, <paramref name="to"/>], newest first, under the
    /// same source filtering as <see cref="GetLatestAsync"/>. Unlike <see cref="GetRecentAsync"/> it is
    /// not capped, so it serves windows of hours or days.
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> GetWindowAsync(DateTime since, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// The newest reading of <paramref name="reading"/>'s stream and data source in [<paramref name="since"/>, the
    /// reading's timestamp], hidden copies included
    /// (<see cref="V4.Repositories.ISensorGlucoseRepository.GetStreamPredecessorAsync"/>).
    /// </summary>
    Task<SensorGlucose?> GetStreamPredecessorAsync(
        SensorGlucose reading, DateTime since, CancellationToken ct = default);

    /// <summary>
    /// The readings of <paramref name="newest"/>'s stream and data source in [<paramref name="since"/>,
    /// the newest reading's timestamp], hidden copies included
    /// (<see cref="V4.Repositories.ISensorGlucoseRepository.GetStreamSeriesAsync"/>).
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> GetStreamSeriesAsync(
        SensorGlucose newest, DateTime since, CancellationToken ct = default);
}
