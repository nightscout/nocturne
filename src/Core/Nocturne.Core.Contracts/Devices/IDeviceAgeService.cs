using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Service to provide device age information using the V4 DeviceEvents system
/// </summary>
public interface IDeviceAgeService
{
    /// <summary>
    /// Get cannula/site age (CAGE).
    /// </summary>
    /// <param name="prefs">User preferences controlling warning and alert thresholds.</param>
    /// <param name="patientDeviceId">Optional registered patient device to scope the underlying device
    /// events to. <c>null</c> uses the most recent matching event tenant-wide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Age information for the cannula/infusion site.</returns>
    Task<DeviceAgeInfo> GetCannulaAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default);

    /// <summary>
    /// Get sensor age (SAGE). Returns a composite with both start and change events.
    /// </summary>
    /// <param name="prefs">User preferences controlling warning and alert thresholds.</param>
    /// <param name="patientDeviceId">Optional registered patient device to scope the underlying device
    /// events to. <c>null</c> uses the most recent matching event tenant-wide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sensor age information including both the session start and the most recent warm-up.</returns>
    Task<SensorAgeInfo> GetSensorAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default);

    /// <summary>
    /// Get insulin reservoir age (IAGE).
    /// </summary>
    /// <param name="prefs">User preferences controlling warning and alert thresholds.</param>
    /// <param name="patientDeviceId">Optional registered patient device to scope the underlying device
    /// events to. <c>null</c> uses the most recent matching event tenant-wide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Age information for the insulin reservoir.</returns>
    Task<DeviceAgeInfo> GetInsulinAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default);

    /// <summary>
    /// Get pump battery age (BAGE).
    /// </summary>
    /// <param name="prefs">User preferences controlling warning and alert thresholds.</param>
    /// <param name="patientDeviceId">Optional registered patient device to scope the underlying device
    /// events to. <c>null</c> uses the most recent matching event tenant-wide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Age information for the pump battery.</returns>
    Task<DeviceAgeInfo> GetBatteryAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default);
}
