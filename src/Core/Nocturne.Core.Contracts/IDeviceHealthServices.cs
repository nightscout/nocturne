using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service interface for device registry and management operations
/// </summary>
public interface IDeviceRegistryService
{
    /// <summary>
    /// Register a new device for a user
    /// </summary>
    /// <param name="userId">User identifier</param>
    /// <param name="request">Device registration request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Registered device health entity</returns>
    /// <exception cref="ArgumentException">Thrown when userId or deviceId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the user has reached the maximum device limit or the device is already registered.</exception>
    Task<DeviceHealth> RegisterDeviceAsync(
        string userId,
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Get all devices for a user
    /// </summary>
    /// <param name="userId">User identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of user's devices</returns>
    /// <exception cref="ArgumentException">Thrown when userId is null or empty.</exception>
    Task<List<DeviceHealth>> GetUserDevicesAsync(
        string userId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Update device health metrics
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="update">Device health update data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    /// <exception cref="ArgumentException">Thrown when deviceId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the device is not found.</exception>
    Task UpdateDeviceHealthAsync(
        string deviceId,
        DeviceHealthUpdate update,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Get device health information
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Device health entity or null if not found</returns>
    /// <exception cref="ArgumentException">Thrown when deviceId is null or empty.</exception>
    Task<DeviceHealth?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Remove a device from the registry
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    /// <exception cref="ArgumentException">Thrown when deviceId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the device is not found.</exception>
    Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Update device settings
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="settings">Device settings update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    /// <exception cref="ArgumentException">Thrown when deviceId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the device is not found.</exception>
    Task UpdateDeviceSettingsAsync(
        string deviceId,
        DeviceSettingsUpdate settings,
        CancellationToken cancellationToken
    );
}



/// <summary>
/// Service interface for device alert engine and smart alerting
/// </summary>
public interface IDeviceAlertEngine
{
    /// <summary>
    /// Process device health and generate appropriate alerts
    /// </summary>
    /// <param name="device">Device health entity</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of generated device alerts</returns>
    /// <exception cref="ArgumentNullException">Thrown when device is null.</exception>
    Task<List<DeviceAlert>> ProcessDeviceAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Check if an alert should be sent based on cooldown and escalation rules
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="alertType">Type of alert</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if alert should be sent</returns>
    /// <exception cref="ArgumentException">Thrown when deviceId is null or empty.</exception>
    Task<bool> ShouldSendAlertAsync(
        string deviceId,
        DeviceAlertType alertType,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Send device alert through appropriate notification channels
    /// </summary>
    /// <param name="deviceAlert">Device alert to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    /// <exception cref="ArgumentNullException">Thrown when deviceAlert is null.</exception>
    Task SendDeviceAlertAsync(DeviceAlert deviceAlert, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledge a device alert
    /// </summary>
    /// <param name="alertId">Alert identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    Task AcknowledgeDeviceAlertAsync(Guid alertId, CancellationToken cancellationToken);
}
