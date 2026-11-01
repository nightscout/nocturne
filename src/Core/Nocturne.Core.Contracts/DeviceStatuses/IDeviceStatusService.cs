using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.DeviceStatuses;

/// <summary>
/// Domain service for legacy <see cref="DeviceStatus"/> documents, which are stored as V4 snapshots
/// and read back as the documents they were decomposed from.
/// </summary>
public interface IDeviceStatusService
{
    /// <summary>
    /// A page of the statuses <paramref name="find"/> matches, newest first or, when
    /// <paramref name="descending"/> is <c>false</c>, the oldest first, evaluated as legacy
    /// Nightscout evaluates it.
    /// </summary>
    /// <param name="find">Parsed with <see cref="FindQuery.ParseDeviceStatus"/>.</param>
    /// <param name="sort">
    /// <see cref="ReadSortField.SrvModified"/> orders by modification stamp when the find is empty or
    /// only bounds <c>srvModified</c> from below; any other field orders by time.
    /// </param>
    Task<IReadOnlyList<DeviceStatus>> GetDeviceStatusesAsync(
        FindQuery find, int count, int skip, CancellationToken cancellationToken,
        bool descending = true, ReadSortField sort = ReadSortField.Time);

    /// <summary>The number of statuses <see cref="GetDeviceStatusesAsync"/> pages through.</summary>
    Task<long> CountDeviceStatusesAsync(FindQuery find, CancellationToken cancellationToken);

    /// <summary>The status <paramref name="id"/> names: its stored id, or the ObjectId its wire form carries.</summary>
    Task<DeviceStatus?> GetDeviceStatusByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Statuses modified after <paramref name="lastModified"/> (Unix milliseconds), for the v3
    /// history endpoint. A deleted status reads with <c>isValid: false</c>.
    /// </summary>
    Task<IEnumerable<DeviceStatus>> GetDeviceStatusesModifiedSinceAsync(
        long lastModified, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Whether storing <paramref name="status"/> writes an APS, pump or uploader snapshot, the rows
    /// that hold a device status under its legacy id. A status carrying none of them leaves nothing a
    /// later lookup by id can find.
    /// </summary>
    bool HasLegacyKeyedSnapshot(DeviceStatus status);

    /// <summary>
    /// Stores each status in turn and announces each one that wrote a record, as reads serve it. A batch
    /// that ends early, cancelled or failed, still announces the statuses it wrote.
    /// </summary>
    /// <remarks>
    /// Every <see cref="WriteOrigin.Live"/> create is announced. The announced statuses carry
    /// <paramref name="source"/> as <see cref="DeviceStatus.DataSource"/>, which the upstream Nightscout
    /// write-back skips for the Nightscout connector's own.
    /// </remarks>
    /// <param name="source">The connector the statuses came from, or <c>null</c> for an upload.</param>
    /// <returns>
    /// The statuses as reads serve them, or as given when not announced, carrying how many of their
    /// records were not written because the user had deleted them.
    /// </returns>
    Task<BulkWrite<DeviceStatus>> CreateDeviceStatusesAsync(
        IReadOnlyList<DeviceStatus> statuses,
        string? source = null,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a batch of statuses imported from another Nightscout, or generated as sample data, as a
    /// backfill that announces nothing.
    /// </summary>
    /// <param name="source">The data source to stamp on the stored snapshots, or <c>null</c>.</param>
    Task<DecompositionResult> ImportDeviceStatusesAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored status <paramref name="id"/> names with <paramref name="status"/>, as a
    /// legacy v3 PUT replaces the whole document: a section it omits is removed.
    /// </summary>
    /// <returns>The replaced status as reads serve it, or <c>null</c> when none is stored under the id.</returns>
    Task<DeviceStatus?> ReplaceDeviceStatusAsync(
        string id, DeviceStatus status, CancellationToken cancellationToken = default);

    /// <summary>Deletes the stored status <paramref name="id"/> names.</summary>
    /// <returns>Whether a status was deleted.</returns>
    Task<bool> DeleteDeviceStatusAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the statuses <paramref name="find"/> matches, at most
    /// <see cref="GetDeviceStatusesAsync"/>'s first 10,000, as legacy Nightscout caps a bulk delete.
    /// </summary>
    /// <returns>The number of statuses deleted.</returns>
    Task<long> DeleteDeviceStatusesAsync(FindQuery find, CancellationToken cancellationToken = default);
}
