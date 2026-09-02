using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Projections;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="SensorGlucose"/> records representing continuous glucose monitor (CGM) readings.
/// </summary>
/// <remarks>
/// This is the V4 native store for CGM data. Legacy V1/V2/V3 SGV entries are projected into this
/// repository so that statistics and chart services have a single source of truth.
/// The <paramref name="nativeOnly"/> flag restricts results to records inserted through the V4 API,
/// excluding projected legacy entries.
/// </remarks>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="IBGCheckRepository"/>
/// <seealso cref="ICalibrationRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ISensorGlucoseRepository : ILegacyKeyedRepository<SensorGlucose>, IDeviceAttributedRepository<SensorGlucose>
{
    /// <summary>
    /// Retrieve a page of <see cref="SensorGlucose"/> records filtered by time range, device, source, and origin.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter (e.g., connector name).</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="nativeOnly">When <c>true</c>, excludes records projected from legacy V1/V2/V3 entries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="patientDeviceId">Optional filter restricting results to a single registered <see cref="PatientDevice"/>'s
    /// attributed readings. Bypasses canonical stream selection at the caller — a filtered read returns that device raw.
    /// Placed after <paramref name="ct"/> so it is additive: existing positional callers (which end at the token) are
    /// unaffected.</param>
    Task<IEnumerable<SensorGlucose>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        DateTime? afterTimestamp = null,
        Guid? afterId = null,
        CancellationToken ct = default,
        Guid? patientDeviceId = null
    );

    // Explicit base-interface bridge — delegates to the extended overload
    Task<IEnumerable<SensorGlucose>> IV4Repository<SensorGlucose>.GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct)
        => GetAsync(from, to, device, source, limit, offset, descending, false, null, null, ct);

    /// <summary>Retrieve all <see cref="SensorGlucose"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SensorGlucose>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Raw-storage duplicate probe for upload idempotency: returns a stored reading matching the
    /// device, value (±0.01 mg/dL), and time window, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetAsync(DateTime?, DateTime?, string?, string?, int, int, bool, bool, DateTime?, Guid?, CancellationToken, Guid?)"/>,
    /// this deliberately includes rows hidden from reads as non-primary cross-connector duplicates.
    /// A hidden copy still means "already stored" — when a second source re-uploads readings whose
    /// copies have all been linked non-primary, a visibility-filtered check finds nothing and the
    /// same readings are re-inserted on every upload cycle.
    /// </remarks>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="mgdl">Optional glucose value to match within ±0.01 mg/dL.</param>
    /// <param name="from">Inclusive start of the time window.</param>
    /// <param name="to">Inclusive end of the time window.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SensorGlucose?> FindStoredDuplicateAsync(
        string? device, double? mgdl, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="SensorGlucose"/> reading, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to determine the last sync time and avoid re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest reading timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the oldest stored <see cref="SensorGlucose"/> reading, optionally scoped to a data source.
    /// </summary>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The oldest reading timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="SensorGlucose"/> records matching the given data source.
    /// </summary>
    /// <param name="source">Data source identifier (e.g., connector name).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default);

    /// <summary>
    /// Lists distinct <c>(DataSource, Device)</c> combinations among unattributed readings
    /// (<c>PatientDeviceId == null</c>) newer than <paramref name="since"/>, with a reading count and
    /// last-seen timestamp per combination. Drives the "discovered sources" device-registration UI.
    /// </summary>
    Task<IReadOnlyList<DiscoveredSource>> GetDiscoveredSourcesAsync(DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Delete all records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
