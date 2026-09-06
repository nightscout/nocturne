using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="DeviceEvent"/> records representing pump and CGM lifecycle events
/// (e.g., site changes, sensor inserts, reservoir fills, and low-reservoir alerts).
/// </summary>
/// <remarks>
/// Device events are used by statistics services to correlate site-change timing with glucose patterns.
/// The <c>GetLatestByEventTypeAsync</c> methods provide efficient lookups for the most recent
/// site change or sensor insertion without fetching the full event history.
/// </remarks>
/// <seealso cref="DeviceEvent"/>
/// <seealso cref="DeviceEventType"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IDeviceEventRepository : ILegacyKeyedRepository<DeviceEvent>, IDeviceAttributionWriter
{
    /// <summary>
    /// Returns unattributed events (<c>PatientDeviceId == null</c>) of the given types within the time
    /// window, newest first, capped at <paramref name="limit"/>. Unlike the other device-attributed
    /// types, one table holds both sensor- and pump-originated events, so back-stamping must narrow to
    /// the types the registering device's category can own.
    /// </summary>
    Task<IReadOnlyList<DeviceEvent>> GetUnattributedAsync(
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<DeviceEventType> eventTypes,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve a page of <see cref="DeviceEvent"/> records filtered by time range, device, source, and origin.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="nativeOnly">When <c>true</c>, excludes records projected from legacy V1/V2/V3 treatments.</param>
    /// <param name="patientDeviceId">Optional filter restricting results to events linked to a single
    /// registered <see cref="PatientDevice"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<DeviceEvent>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        Guid? patientDeviceId = null,
        CancellationToken ct = default
    );

    // Explicit base-interface bridge — delegates to the extended overload
    Task<IEnumerable<DeviceEvent>> IV4Repository<DeviceEvent>.GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct)
        => GetAsync(from, to, device, source, limit, offset, descending, nativeOnly: false, patientDeviceId: null, ct: ct);

    /// <summary>Delete <see cref="DeviceEvent"/> records matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="DeviceEvent"/>, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to resume per-source sync without re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="DeviceEvent"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<DeviceEvent>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Retrieve the most recent <see cref="DeviceEvent"/> of the specified <see cref="DeviceEventType"/>,
    /// optionally pinned to a historical instant.
    /// </summary>
    /// <param name="eventType">The <see cref="DeviceEventType"/> to search for (e.g., site change).</param>
    /// <param name="asOf">When non-null, restricts to events with <c>Timestamp &lt;= asOf</c>; powers
    /// replay's <c>site_age</c> / <c>sensor_age</c> reconstruction. <c>null</c> returns the
    /// absolute latest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent matching event, or <c>null</c> if none exists.</returns>
    Task<DeviceEvent?> GetLatestByEventTypeAsync(DeviceEventType eventType, DateTime? asOf, CancellationToken ct = default);

    /// <summary>Convenience overload returning the absolute latest event of the given type.</summary>
    Task<DeviceEvent?> GetLatestByEventTypeAsync(DeviceEventType eventType, CancellationToken ct = default)
        => GetLatestByEventTypeAsync(eventType, asOf: null, ct);

    /// <summary>
    /// Retrieve the most recent <see cref="DeviceEvent"/> matching any of the specified <see cref="DeviceEventType"/> values.
    /// </summary>
    /// <param name="eventTypes">Array of <see cref="DeviceEventType"/> values to search for.</param>
    /// <param name="patientDeviceId">Optional filter restricting the search to events linked to a single
    /// registered patient device. Pass <c>null</c> to search tenant-wide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent matching event, or <c>null</c> if none exists.</returns>
    Task<DeviceEvent?> GetLatestByEventTypesAsync(
        DeviceEventType[] eventTypes,
        Guid? patientDeviceId = null,
        CancellationToken ct = default
    );
}
