using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

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
public interface IDeviceEventRepository : IV4Repository<DeviceEvent>
{
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
        CancellationToken ct = default
    );

    // Explicit base-interface bridge — delegates to the extended overload
    Task<IEnumerable<DeviceEvent>> IV4Repository<DeviceEvent>.GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct)
        => GetAsync(from, to, device, source, limit, offset, descending, false, ct);

    /// <summary>Returns a single <see cref="DeviceEvent"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<DeviceEvent?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="DeviceEvent"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<DeviceEvent?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="DeviceEvent"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<DeviceEvent> CreateAsync(DeviceEvent model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="DeviceEvent"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<DeviceEvent> UpdateAsync(Guid id, DeviceEvent model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="DeviceEvent"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="DeviceEvent"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Delete <see cref="DeviceEvent"/> records matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>Count <see cref="DeviceEvent"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="DeviceEvent"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<DeviceEvent>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>Insert multiple <see cref="DeviceEvent"/> records in a single batch operation.</summary>
    /// <param name="records">Records to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<DeviceEvent>> BulkCreateAsync(
        IEnumerable<DeviceEvent> records,
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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent matching event, or <c>null</c> if none exists.</returns>
    Task<DeviceEvent?> GetLatestByEventTypesAsync(DeviceEventType[] eventTypes, CancellationToken ct = default);
}
