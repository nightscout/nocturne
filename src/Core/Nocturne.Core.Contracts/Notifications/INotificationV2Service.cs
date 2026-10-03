using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Notifications;

/// <summary>
/// Service interface for V2 notification operations with 1:1 legacy JavaScript compatibility
/// Handles Loop notifications and enhanced notification system features
/// </summary>
public interface INotificationV2Service
{
    /// <summary>
    /// Pushes a Loop remote command to the phone named by the current profile's
    /// <c>loopSettings</c>, as Nightscout's <c>loop.sendNotification</c> does.
    /// </summary>
    Task<LoopNotificationResponse> SendLoopNotificationAsync(
        LoopNotificationData data,
        string remoteAddress,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Processes a generic V2 notification
    /// </summary>
    /// <param name="notification">Base notification data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Notification response indicating success or failure</returns>
    Task<NotificationV2Response> ProcessNotificationAsync(
        NotificationBase notification,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets the current notification status and configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current notification system status</returns>
    Task<object> GetNotificationStatusAsync(CancellationToken cancellationToken = default);
}
