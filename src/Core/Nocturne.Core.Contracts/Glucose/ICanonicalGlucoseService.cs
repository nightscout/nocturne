using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Applies canonical stream selection (<see cref="CanonicalGlucoseStream"/>) to fetched glucose
/// readings, loading the patient's registered devices for the priority order.
/// </summary>
/// <remarks>
/// The canonical view is what single-stream consumers see: v1/v3 REST, the legacy realtime
/// broadcast, alarms, and unfiltered statistics. Multi-stream access is a V4-only concern.
/// </remarks>
/// <seealso cref="CanonicalGlucoseStream"/>
public interface ICanonicalGlucoseService
{
    /// <summary>
    /// Filters <paramref name="readings"/> to the canonical stream. Input order is preserved;
    /// a single-stream input is returned unchanged without loading devices.
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> SelectAsync(
        IReadOnlyList<SensorGlucose> readings,
        CancellationToken ct = default);

    /// <summary>
    /// The most recent canonical reading, however old — computed over the newest stored readings
    /// so delayed-sync sources (hours-late connector backfills) still surface a latest value.
    /// Null only when the tenant has no reading that counts. Staleness judgement belongs to the
    /// caller, exactly as it did when consumers used the newest reading of a write batch.
    /// Readings that don't count in statistics
    /// (<see cref="SensorGlucoseQualityRules.CountsInStatistics"/>) are skipped before the newest
    /// rows are taken, as alarms act on this value: during a sensor-error episode of any length
    /// the last trusted reading ages, and the signal-loss and staleness alerts take over.
    /// </summary>
    Task<SensorGlucose?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>
    /// Canonical readings timestamped at or after <paramref name="since"/>, newest first, under the
    /// same filtering as <see cref="GetLatestAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SensorGlucose>> GetRecentAsync(DateTime since, CancellationToken ct = default);
}
