using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Reads what each data source has stored in the tenant's data tables, as the scope's reader.
/// </summary>
/// <remarks>
/// A source is matched on either origin handle, <c>DataSource</c> or <c>Device</c>, and a state span
/// on its <c>Source</c>.
/// </remarks>
public interface ISourceOwnedDataReader
{
    /// <summary>Sensor glucose from <paramref name="windowStart"/> on, by non-empty <c>Device</c>, in device order.</summary>
    Task<IReadOnlyList<GlucoseDeviceStats>> GlucoseDevicesAsync(
        DateTime windowStart, DateTime last24Hours, CancellationToken ct);

    /// <summary>APS snapshots from <paramref name="windowStart"/> on, by non-empty <c>Device</c>.</summary>
    Task<IReadOnlyList<ApsDeviceLastSeen>> ApsSnapshotDevicesAsync(DateTime windowStart, CancellationToken ct);

    /// <summary>
    /// The buckets of meter glucose and of every non-glucose table discovery lists, from
    /// <paramref name="windowStart"/> on, one per table and key.
    /// </summary>
    Task<IReadOnlyList<SourceBucket>> BucketsAsync(
        DateTime windowStart, DateTime last24Hours, CancellationToken ct);

    /// <summary>Every row <paramref name="source"/> owns, counted by data type, non-zero only.</summary>
    Task<IReadOnlyDictionary<string, long>> CountsBySourceAsync(string source, CancellationToken ct);

    /// <summary>
    /// The per-type breakdown of a source's statistics: sensor glucose and state spans with their
    /// latest and oldest times, then meter glucose, the treatment types and APS snapshots.
    /// </summary>
    Task<IReadOnlyList<SourceTypeStats>> StatsBySourceAsync(
        string source, DateTime last24Hours, CancellationToken ct);

    /// <summary>The latest time among the source's treatment rows, or null when it has none.</summary>
    Task<DateTime?> LatestTreatmentTimestampAsync(string source, CancellationToken ct);

    /// <summary>The oldest time among the source's treatment rows, or null when it has none.</summary>
    Task<DateTime?> OldestTreatmentTimestampAsync(string source, CancellationToken ct);
}
