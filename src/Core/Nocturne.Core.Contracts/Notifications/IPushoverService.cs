using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Notifications;

/// <summary>
/// Interface for Pushover notification service
/// Provides functionality for sending notifications to Pushover API with 1:1 legacy compatibility
/// </summary>
public interface IPushoverService
{
    /// <summary>
    /// Sends a Pushover notification
    /// Maintains 1:1 compatibility with legacy Pushover notification sending
    /// </summary>
    /// <returns>Pushover response with receipt information</returns>
    Task<PushoverResponse> SendNotificationAsync(
        PushoverNotificationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a Pushover notification request from alarm details
    /// Implements legacy alarm-to-Pushover mapping logic
    /// </summary>
    /// <param name="level">Alarm level (1=WARN, 2=URGENT)</param>
    PushoverNotificationRequest CreateAlarmNotification(
        int level,
        string group,
        string title,
        string message,
        string? sound = null
    );
}
