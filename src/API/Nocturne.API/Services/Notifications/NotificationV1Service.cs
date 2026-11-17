using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// In-memory alarm and admin-notification service with 1:1 legacy JavaScript compatibility.
/// Implements functionality from <c>notifications.js</c> and <c>adminnotifies.js</c>. Alarm snoozes
/// live in <see cref="LegacyAlarmSnoozeStore"/>, admin notifications in
/// <see cref="LegacyNotificationStore"/>.
/// </summary>
/// <seealso cref="INotificationV1Service"/>
public class NotificationV1Service : INotificationV1Service
{
    private readonly ILogger<NotificationV1Service> _logger;
    private readonly IAuthorizationService _authorizationService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly LegacyAlarmSnoozeStore _snoozes;
    private readonly LegacyNotificationStore _notifications;
    private readonly TimeProvider _time;

    public NotificationV1Service(
        ILogger<NotificationV1Service> logger,
        IAuthorizationService authorizationService,
        ISignalRBroadcastService signalRBroadcastService,
        ITenantAccessor tenantAccessor,
        LegacyAlarmSnoozeStore snoozes,
        LegacyNotificationStore notifications,
        TimeProvider timeProvider
    )
    {
        _logger = logger;
        _authorizationService = authorizationService;
        _signalRBroadcastService = signalRBroadcastService;
        _tenantAccessor = tenantAccessor;
        _snoozes = snoozes;
        _notifications = notifications;
        _time = timeProvider;
    }

    public async Task<NotificationAckResponse> AckNotificationAsync(
        NotificationAckRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Processing notification acknowledgment for level {Level}, group {Group}",
                request.Level,
                request.Group
            );

            var currentTime = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var silenceTime = request.Time ?? NotificationAckRequest.DefaultSilenceTimeMs;

            if (!_snoozes.TrySnooze(_tenantAccessor.TenantId, request.Level, request.Group, silenceTime))
            {
                var message =
                    $"Alarm has already been snoozed, don't snooze it again, level: {request.Level}, group: {request.Group}";
                _logger.LogWarning(message);
                return new NotificationAckResponse
                {
                    Success = false,
                    Message = message,
                    Timestamp = currentTime,
                };
            }

            _logger.LogInformation(
                "Acknowledged alarm - level: {Level}, group: {Group}, silenceTime: {SilenceTime}ms",
                request.Level,
                request.Group,
                silenceTime
            );

            // Send clear alarm notification if requested (replaces legacy ctx.bus.emit('notification', notify))
            if (request.SendClear == true)
            {
                var clearNotification = new NotificationBase
                {
                    Clear = true,
                    Group = request.Group,
                    Level = request.Level,
                    Title = $"Alarm Cleared - {request.Group}",
                    Message = $"Level {request.Level} alarm has been acknowledged and cleared",
                    Timestamp = currentTime,
                };

                try
                {
                    await _signalRBroadcastService.BroadcastClearAlarmAsync(clearNotification);
                    _logger.LogDebug(
                        "Broadcasted clear alarm event for level {Level}, group {Group}",
                        request.Level,
                        request.Group
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to broadcast clear alarm event for level {Level}, group {Group}",
                        request.Level,
                        request.Group
                    );
                }
            }

            // If level 2 (URGENT), also acknowledge level 1 (WARN) - legacy behavior
            if (request.Level == 2)
            {
                await AckNotificationAsync(
                    new NotificationAckRequest
                    {
                        Level = 1,
                        Group = request.Group,
                        Time = request.Time,
                    },
                    cancellationToken
                );
            }

            return new NotificationAckResponse
            {
                Success = true,
                Message = $"Acknowledged {request.Group} - Level {request.Level}",
                Timestamp = currentTime,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error acknowledging notification for level {Level}, group {Group}",
                request.Level,
                request.Group
            );
            return new NotificationAckResponse
            {
                Success = false,
                Message = "Internal error processing acknowledgment",
                Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
            };
        }
    }

    public async Task<AdminNotifiesResponse> GetAdminNotifiesAsync(
        string? subjectId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Retrieving admin notifications for subject {SubjectId}",
                subjectId ?? "anonymous"
            );

            var notifications = _notifications.ListAdminNotifications(_tenantAccessor.TenantId);

            // Check if user has admin permissions (implements legacy authorization check)
            var isAdmin = false;
            if (!string.IsNullOrEmpty(subjectId))
            {
                // Check for full admin permissions equivalent to '*:*:admin' in the legacy system
                isAdmin = await _authorizationService.CheckPermissionAsync(subjectId, "*:*:admin");
                _logger.LogDebug(
                    "Admin permission check for subject {SubjectId}: {IsAdmin}",
                    subjectId,
                    isAdmin
                );
            }

            var response = new AdminNotifiesResponse
            {
                Status = 200,
                Message = new AdminNotifiesMessage
                {
                    // Only return notification details to admin users (1:1 legacy compatibility)
                    Notifies = isAdmin ? notifications : new List<AdminNotification>(),
                    NotifyCount = notifications.Count,
                },
            };

            _logger.LogDebug(
                "Returning {Count} notifications, details visible: {IsAdmin}",
                notifications.Count,
                isAdmin
            );

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving admin notifications");
            return new AdminNotifiesResponse
            {
                Status = 500,
                Message = new AdminNotifiesMessage
                {
                    Notifies = new List<AdminNotification>(),
                    NotifyCount = 0,
                },
            };
        }
    }

    public Task<NotificationAckResponse> AddAdminNotificationAsync(
        AdminNotification notification,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (notification == null)
            {
                return Task.FromResult(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Notification cannot be null",
                        Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                    }
                );
            }

            // Implement legacy adminnotifies.addNotify() behavior
            notification.Title = string.IsNullOrEmpty(notification.Title)
                ? "None"
                : notification.Title;
            notification.Message = string.IsNullOrEmpty(notification.Message)
                ? "None"
                : notification.Message;

            var count = _notifications.AddAdminNotification(_tenantAccessor.TenantId, notification);
            _logger.LogDebug(
                "Recorded admin notification: {Message}, count: {Count}",
                notification.Message,
                count
            );

            return Task.FromResult(
                new NotificationAckResponse
                {
                    Success = true,
                    Message = "Admin notification added successfully",
                    Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding admin notification");
            return Task.FromResult(
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal error adding admin notification",
                    Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    public Task ClearAllAdminNotificationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Clear all notifications
            _notifications.ClearAdminNotifications(_tenantAccessor.TenantId);

            _logger.LogInformation("Cleared all admin notifications");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing all admin notifications");
            return Task.CompletedTask;
        }
    }
}
