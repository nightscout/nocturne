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
    /// Canonical readings timestamped at or after <paramref name="since"/>, newest first, under the
    /// same source filtering as <see cref="GetLatestAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> GetRecentAsync(DateTime since, CancellationToken ct = default);
}
