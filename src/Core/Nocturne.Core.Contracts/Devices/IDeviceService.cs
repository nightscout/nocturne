using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Service for resolving physical devices to their canonical <see cref="Guid"/> identifiers.
/// Devices are identified by a combination of <see cref="DeviceCategory"/>, type, and serial number.
/// </summary>
/// <seealso cref="DeviceCategory"/>
public interface IDeviceService
{
    /// <summary>
    /// Resolve a physical device to its canonical ID, creating the device record if it does not already exist.
    /// </summary>
    /// <param name="category">The <see cref="DeviceCategory"/> (e.g., Pump, CGM).</param>
    /// <param name="type">Device type or model name (e.g., "Omnipod 5").</param>
    /// <param name="serial">Device serial number, or <c>null</c> if unknown.</param>
    /// <param name="mills">Timestamp in Unix milliseconds when this device was seen.</param>
    /// <returns>The canonical device <see cref="Guid"/>, or <c>null</c> if resolution failed.</returns>
    Task<Guid?> ResolveAsync(DeviceCategory category, string? type, string? serial, long mills, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ResolveAsync"/> for many sightings at once: one lookup for the identities not yet
    /// resolved in this scope, a create per identity never stored, and each device's seen window
    /// widened once to the earliest and latest of its sightings.
    /// </summary>
    /// <returns>Per sighting, in order, its device's id, or <c>null</c> where it names no device.</returns>
    Task<IReadOnlyList<Guid?>> ResolveManyAsync(IReadOnlyList<DeviceSighting> sightings, CancellationToken ct = default);

    /// <summary>
    /// The patient-device links of <paramref name="deviceIds"/>, read in one query for the ids not yet
    /// read in this scope, to resolve each record's patient device without a read each.
    /// </summary>
    Task<IPatientDeviceLinks> GetPatientDeviceLinksAsync(IEnumerable<Guid?> deviceIds, CancellationToken ct = default);
}

/// <summary>A device seen at <see cref="Mills"/>, as <see cref="IDeviceService.ResolveAsync"/> takes it.</summary>
public readonly record struct DeviceSighting(DeviceCategory Category, string? Type, string? Serial, long Mills);

/// <summary>The links <see cref="IDeviceService.GetPatientDeviceLinksAsync"/> read.</summary>
public interface IPatientDeviceLinks
{
    /// <summary>
    /// The <see cref="PatientDevice"/> linked to <paramref name="deviceId"/> whose date range contains
    /// <paramref name="mills"/>; <c>null</c> for no match, a null device, or a device whose links were
    /// not read.
    /// </summary>
    Guid? Resolve(Guid? deviceId, long mills);
}
