using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IDevicePublisher
{
    Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the device statuses a source may already have delivered that nothing stored holds yet
    /// under their id. A held status is left as it is stored, and one the user deleted does not come back.
    /// </summary>
    /// <returns>How many were written, or null when the write failed.</returns>
    Task<int?> PublishRecentDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        WriteOrigin origin,
        CancellationToken cancellationToken = default);

    Task<bool> PublishDeviceEventsAsync(
        IEnumerable<DeviceEvent> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the patient's hardware inventory (pumps, CGMs) as <see cref="PatientDevice"/> records,
    /// keyed by the deterministic <see cref="PatientDevice.Id"/> so re-syncs update in place rather
    /// than duplicating.
    /// </summary>
    Task<bool> PublishPatientDevicesAsync(
        IEnumerable<PatientDevice> devices,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the timestamp of the most recent device-status record for the current tenant,
    /// used by connectors to resume catch-up from where they left off, or <c>null</c> if none exist.
    /// </summary>
    Task<DateTime?> GetLatestDeviceStatusTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}
