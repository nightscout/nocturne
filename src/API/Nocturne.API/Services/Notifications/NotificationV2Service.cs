using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// V2 notification service with 1:1 legacy JavaScript compatibility. Implements the enhanced
/// notification system from <c>notifications-v2.js</c>, including Loop APNS push notification
/// dispatch via <see cref="ILoopService"/> and a three-level urgency model (INFO, WARN, URGENT).
/// </summary>
/// <seealso cref="INotificationV2Service"/>
public class NotificationV2Service : INotificationV2Service
{
    private readonly ILogger<NotificationV2Service> _logger;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly IProfileProjectionService _profileProjectionService;
    private readonly ILoopService _loopService;

    // Notification levels (replaces legacy notification levels)
    private readonly NotificationLevels _levels = new()
    {
        INFO = 0,
        WARN = 1,
        URGENT = 2,
    };

    public NotificationV2Service(
        ILogger<NotificationV2Service> logger,
        ISignalRBroadcastService signalRBroadcastService,
        IProfileProjectionService profileProjectionService,
        ILoopService loopService
    )
    {
        _logger = logger;
        _signalRBroadcastService = signalRBroadcastService;
        _profileProjectionService = profileProjectionService;
        _loopService = loopService;
    }

    /// <summary>
    /// Notification levels constants for compatibility
    /// </summary>
    private class NotificationLevels
    {
        public int INFO { get; set; }
        public int WARN { get; set; }
        public int URGENT { get; set; }
    }

    public async Task<LoopNotificationResponse> SendLoopNotificationAsync(
        LoopNotificationData data,
        string remoteAddress,
        CancellationToken cancellationToken = default
    )
    {
        var profile = await _profileProjectionService.GetCurrentProfileAsync(cancellationToken);

        return await _loopService.SendNotificationAsync(
            data,
            profile?.LoopSettings,
            remoteAddress,
            cancellationToken
        );
    }

    /// <summary>
    /// Processes a generic V2 notification
    /// </summary>
    /// <param name="notification">Base notification data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Notification response indicating success or failure</returns>
    public async Task<NotificationV2Response> ProcessNotificationAsync(
        NotificationBase notification,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Processing V2 notification: {Title} - {Message}",
            notification.Title,
            notification.Message
        );

        try
        {
            // Validate the notification
            if (
                string.IsNullOrEmpty(notification.Title)
                && string.IsNullOrEmpty(notification.Message)
            )
            {
                return new NotificationV2Response
                {
                    Success = false,
                    Message = "Notification must have either title or message",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
            }

            // Set default values
            notification.Title = notification.Title ?? "Notification";
            notification.Message = notification.Message ?? "";
            notification.Group = notification.Group ?? "default";
            notification.Timestamp =
                notification.Timestamp == 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    : notification.Timestamp;

            // Process the notification (this would integrate with actual notification systems)
            await ProcessNotificationInternalAsync(notification, cancellationToken);

            _logger.LogInformation(
                "Successfully processed V2 notification: {Title}",
                notification.Title
            );

            return new NotificationV2Response
            {
                Success = true,
                Message = "Notification processed successfully",
                Data = new
                {
                    title = notification.Title,
                    group = notification.Group,
                    level = notification.Level,
                    processed_at = DateTimeOffset.UtcNow.ToString("O"),
                },
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing V2 notification: {Title} - {Message}",
                notification.Title,
                notification.Message
            );

            return new NotificationV2Response
            {
                Success = false,
                Message = "Internal error processing notification",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
    }

    /// <summary>
    /// Gets the current notification status and configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current notification system status</returns>
    public async Task<object> GetNotificationStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        await Task.CompletedTask; // Placeholder for async operation

        return new
        {
            status = "active",
            version = "v2",
            supported_types = new[] { "loop", "announcement", "alarm", "info" },
            capabilities = new
            {
                loop_integration = _loopService.IsConfigurationValid(),
                push_notifications = _loopService.IsConfigurationValid(),
                email_notifications = false, // Would be true if email service is configured
                websocket_notifications = true,
            },
            last_update = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Internal method to process generic notifications
    /// </summary>
    /// <param name="notification">Notification to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ProcessNotificationInternalAsync(
        NotificationBase notification,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation(
            "V2 notification processed: Title={Title}, Level={Level}, Group={Group}, Plugin={Plugin}",
            notification.Title,
            notification.Level,
            notification.Group,
            notification.Plugin
        );

        // Broadcast appropriate WebSocket event based on notification properties (replaces legacy ctx.bus.emit('notification', notify))
        try
        {
            if (notification.Clear)
            {
                await _signalRBroadcastService.BroadcastClearAlarmAsync(notification);
            }
            else if (notification.IsAnnouncement)
            {
                await _signalRBroadcastService.BroadcastAnnouncementAsync(notification);
            }
            else if (notification.Level == _levels.URGENT)
            {
                await _signalRBroadcastService.BroadcastUrgentAlarmAsync(notification);
            }
            else if (notification.Level == _levels.WARN)
            {
                await _signalRBroadcastService.BroadcastAlarmAsync(notification);
            }
            else
            {
                await _signalRBroadcastService.BroadcastNotificationAsync(notification);
            }

            _logger.LogDebug(
                "Successfully broadcasted WebSocket notification event for {Title}",
                notification.Title
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to broadcast notification WebSocket event for {Title}",
                notification.Title
            );
        }
    }
}
