using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IDevicePublisher
{
    Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishDeviceEventsAsync(
        IEnumerable<DeviceEvent> records,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the timestamp of the most recent device-status record for the current tenant,
    /// used by connectors to resume catch-up from where they left off, or <c>null</c> if none exist.
    /// </summary>
    Task<DateTime?> GetLatestDeviceStatusTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}
