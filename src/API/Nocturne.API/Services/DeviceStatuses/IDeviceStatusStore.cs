using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>
/// Device status reads over the V4 snapshot tables, projected to the legacy <see cref="DeviceStatus"/>
/// shape; writes go through <see cref="IDeviceStatusDecomposer"/>.
/// </summary>
internal interface IDeviceStatusStore
{
    /// <summary>
    /// A page of the statuses <paramref name="find"/> matches, newest first or, when
    /// <paramref name="descending"/> is <c>false</c>, the oldest first, evaluated as legacy
    /// Nightscout evaluates it. The device equality and time bounds filter the store; every
    /// condition is tested against the projected document.
    /// </summary>
    /// <param name="sort">
    /// <see cref="ReadSortField.SrvModified"/> orders by modification stamp when the find is empty or
    /// only bounds <c>srvModified</c> from below, which is served by the modified-since pages; any
    /// other field orders by time.
    /// </param>
    Task<IReadOnlyList<DeviceStatus>> GetMatchingAsync(
        FindQuery find, int count, int skip, CancellationToken ct,
        bool descending = true, ReadSortField sort = ReadSortField.Time);

    /// <summary>The number of statuses <see cref="GetMatchingAsync"/> pages through.</summary>
    Task<long> CountMatchingAsync(FindQuery find, CancellationToken ct);

    /// <summary>The status <paramref name="id"/> names: its stored id, or the ObjectId its wire form carries.</summary>
    Task<DeviceStatus?> GetByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// Returns <see cref="DeviceStatus"/> documents modified since the given Unix millisecond
    /// threshold. Used for AAPS incremental sync. A deleted status projects with <c>isValid: false</c>.
    /// </summary>
    /// <param name="lastModified">Unix millisecond timestamp threshold.</param>
    /// <param name="limit">Maximum number of results.</param>
    Task<IEnumerable<DeviceStatus>> GetModifiedSinceAsync(long lastModified, int limit, CancellationToken ct);
}
