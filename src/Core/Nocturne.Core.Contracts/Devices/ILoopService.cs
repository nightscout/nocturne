using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Service interface for Loop notification operations with 1:1 legacy JavaScript compatibility
/// Handles Apple Push Notification Service (APNS) integration for iOS Loop app notifications
/// Based on the legacy loop.js implementation
/// </summary>
public interface ILoopService
{
    /// <summary>
    /// Sends a Loop notification via Apple Push Notification Service (APNS)
    /// Implements the legacy loop.sendNotification() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="data">Loop notification data containing event type and parameters</param>
    /// <param name="loopSettings">Loop settings from user profile containing device token and bundle ID</param>
    /// <param name="remoteAddress">IP address of the requesting client</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Loop notification response indicating success or failure</returns>
    Task<LoopNotificationResponse> SendNotificationAsync(
        LoopNotificationData data,
        LoopProfileSettings? loopSettings,
        string remoteAddress,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Validates Loop configuration settings
    /// Checks that all required APNS configuration is present
    /// </summary>
    /// <returns>True if configuration is valid, false otherwise</returns>
    bool IsConfigurationValid();

    /// <summary>
    /// Gets the current Loop configuration status for debugging
    /// </summary>
    /// <returns>Configuration status information</returns>
    object GetConfigurationStatus();
}
