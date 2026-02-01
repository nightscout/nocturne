using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Service for broadcasting real-time updates via SignalR (replaces socket.io server-side broadcasting)
/// </summary>
public interface ISignalRBroadcastService
{
    /// <summary>
    /// Broadcast data update to authorized clients (replaces socket.io 'dataUpdate' event)
    /// </summary>
    Task BroadcastDataUpdateAsync(object data);

    /// <summary>
    /// Broadcast retro data update to specific client (replaces socket.io 'retroUpdate' event)
    /// </summary>
    Task BroadcastRetroUpdateAsync(string connectionId, object retroData);

    /// <summary>
    /// Broadcast notification to alarm subscribers (replaces socket.io 'notification' event)
    /// </summary>
    Task BroadcastNotificationAsync(NotificationBase notification);

    /// <summary>
    /// Broadcast announcement to alarm subscribers (replaces socket.io 'announcement' event)
    /// </summary>
    Task BroadcastAnnouncementAsync(NotificationBase announcement);

    /// <summary>
    /// Broadcast alarm to alarm subscribers (replaces socket.io 'alarm' event)
    /// </summary>
    Task BroadcastAlarmAsync(NotificationBase alarm);

    /// <summary>
    /// Broadcast urgent alarm to alarm subscribers (replaces socket.io 'urgent_alarm' event)
    /// </summary>
    Task BroadcastUrgentAlarmAsync(NotificationBase urgentAlarm);

    /// <summary>
    /// Broadcast clear alarm to alarm subscribers (replaces socket.io 'clear_alarm' event)
    /// </summary>
    Task BroadcastClearAlarmAsync(NotificationBase clearAlarm);

    // Storage events
    /// <summary>
    /// Broadcast storage create event (replaces socket.io 'create' event in storage namespace)
    /// </summary>
    Task BroadcastStorageCreateAsync(string collectionName, object data);

    /// <summary>
    /// Broadcast storage update event (replaces socket.io 'update' event in storage namespace)
    /// </summary>
    Task BroadcastStorageUpdateAsync(string collectionName, object data);

    /// <summary>
    /// Broadcast storage delete event (replaces socket.io 'delete' event in storage namespace)
    /// </summary>
    Task BroadcastStorageDeleteAsync(string collectionName, object data);

    /// <summary>
    /// Broadcast tracker update to authorized clients (for real-time tracker notifications)
    /// </summary>
    Task BroadcastTrackerUpdateAsync(string action, object trackerInstance);

    /// <summary>
    /// Broadcast password reset request to admin subscribers via DataHub
    /// </summary>
    Task BroadcastPasswordResetRequestAsync();

    /// <summary>
    /// Broadcast configuration change event to subscribers via ConfigHub
    /// </summary>
    Task BroadcastConfigChangeAsync(ConfigurationChangeEvent change);

    /// <summary>
    /// Broadcast notification created event to a specific user
    /// </summary>
    /// <param name="userId">The user ID to broadcast to</param>
    /// <param name="notification">The notification that was created</param>
    Task BroadcastNotificationCreatedAsync(string userId, InAppNotificationDto notification);

    /// <summary>
    /// Broadcast notification archived event to a specific user
    /// </summary>
    /// <param name="userId">The user ID to broadcast to</param>
    /// <param name="notification">The notification that was archived</param>
    /// <param name="archiveReason">The reason the notification was archived</param>
    Task BroadcastNotificationArchivedAsync(string userId, InAppNotificationDto notification, NotificationArchiveReason archiveReason);

    /// <summary>
    /// Broadcast notification updated event to a specific user
    /// </summary>
    /// <param name="userId">The user ID to broadcast to</param>
    /// <param name="notification">The notification that was updated</param>
    Task BroadcastNotificationUpdatedAsync(string userId, InAppNotificationDto notification);
}

/// <summary>
/// Implementation of SignalR broadcast service
/// </summary>
public class SignalRBroadcastService : ISignalRBroadcastService
{
    private readonly IHubContext<DataHub> _dataHubContext;
    private readonly IHubContext<AlarmHub> _alarmHubContext;
    private readonly IHubContext<ConfigHub> _configHubContext;
    private readonly ILogger<SignalRBroadcastService> _logger;

    public SignalRBroadcastService(
        IHubContext<DataHub> dataHubContext,
        IHubContext<AlarmHub> alarmHubContext,
        IHubContext<ConfigHub> configHubContext,
        ILogger<SignalRBroadcastService> logger
    )
    {
        _dataHubContext = dataHubContext;
        _alarmHubContext = alarmHubContext;
        _configHubContext = configHubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task BroadcastDataUpdateAsync(object data)
    {
        try
        {
            _logger.LogInformation(
                "Broadcasting data update to authorized clients: {DataType}",
                data?.GetType().Name ?? "null"
            );
            await _dataHubContext
                .Clients.Group("authorized")
                .SendCoreAsync("dataUpdate", new[] { data });
            _logger.LogInformation("Data update broadcast completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting data update");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastRetroUpdateAsync(string connectionId, object retroData)
    {
        try
        {
            _logger.LogDebug("Broadcasting retro update to client {ConnectionId}", connectionId);
            await _dataHubContext
                .Clients.Client(connectionId)
                .SendCoreAsync("retroUpdate", new[] { retroData });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting retro update to client {ConnectionId}",
                connectionId
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastNotificationAsync(NotificationBase notification)
    {
        try
        {
            _logger.LogDebug("Broadcasting notification: {Title}", notification.Title);
            await _alarmHubContext
                .Clients.Group("alarm-subscribers")
                .SendCoreAsync("notification", new[] { notification });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting notification");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastAnnouncementAsync(NotificationBase announcement)
    {
        try
        {
            _logger.LogDebug("Broadcasting announcement: {Title}", announcement.Title);
            await _alarmHubContext
                .Clients.Group("alarm-subscribers")
                .SendCoreAsync("announcement", new[] { announcement });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting announcement");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastAlarmAsync(NotificationBase alarm)
    {
        try
        {
            _logger.LogDebug("Broadcasting alarm: {Title}", alarm.Title);
            await _alarmHubContext
                .Clients.Group("alarm-subscribers")
                .SendCoreAsync("alarm", new[] { alarm });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting alarm");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastUrgentAlarmAsync(NotificationBase urgentAlarm)
    {
        try
        {
            _logger.LogDebug("Broadcasting urgent alarm: {Title}", urgentAlarm.Title);
            await _alarmHubContext
                .Clients.Group("alarm-subscribers")
                .SendCoreAsync("urgent_alarm", new[] { urgentAlarm });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting urgent alarm");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastClearAlarmAsync(NotificationBase clearAlarm)
    {
        try
        {
            _logger.LogDebug("Broadcasting clear alarm: {Title}", clearAlarm.Title);
            await _alarmHubContext
                .Clients.Group("alarm-subscribers")
                .SendCoreAsync("clear_alarm", new[] { clearAlarm });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting clear alarm");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastStorageCreateAsync(string collectionName, object data)
    {
        try
        {
            await _dataHubContext
                .Clients.Group(collectionName)
                .SendCoreAsync("create", new[] { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting storage create event for collection {Collection}",
                collectionName
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastStorageUpdateAsync(string collectionName, object data)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting storage update event for collection {Collection}",
                collectionName
            );
            await _dataHubContext
                .Clients.Group(collectionName)
                .SendCoreAsync("update", new[] { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting storage update event for collection {Collection}",
                collectionName
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastStorageDeleteAsync(string collectionName, object data)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting storage delete event for collection {Collection}",
                collectionName
            );
            await _dataHubContext
                .Clients.Group(collectionName)
                .SendCoreAsync("delete", new[] { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting storage delete event for collection {Collection}",
                collectionName
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastTrackerUpdateAsync(string action, object trackerInstance)
    {
        try
        {
            _logger.LogInformation(
                "Broadcasting tracker update event: {Action}",
                action
            );
            var payload = new { action, instance = trackerInstance };
            await _dataHubContext
                .Clients.Group("authorized")
                .SendCoreAsync("trackerUpdate", new[] { payload });
            _logger.LogDebug("Tracker update broadcast completed for action {Action}", action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting tracker update event");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastPasswordResetRequestAsync()
    {
        try
        {
            _logger.LogInformation("Broadcasting password reset request to admin subscribers");
            await _dataHubContext
                .Clients.Group("admin")
                .SendCoreAsync("passwordResetRequested", Array.Empty<object>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting password reset request");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastConfigChangeAsync(ConfigurationChangeEvent change)
    {
        try
        {
            _logger.LogInformation(
                "Broadcasting config change for {ConnectorName}: {ChangeType}",
                change.ConnectorName,
                change.ChangeType
            );

            // Broadcast to connector-specific group
            var connectorGroup = $"config:{change.ConnectorName.ToLowerInvariant()}";
            await _configHubContext
                .Clients.Group(connectorGroup)
                .SendCoreAsync("configChanged", new[] { change });

            // Also broadcast to "all" subscribers
            await _configHubContext
                .Clients.Group("config:all")
                .SendCoreAsync("configChanged", new[] { change });

            _logger.LogDebug("Config change broadcast completed for {ConnectorName}", change.ConnectorName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting config change for {ConnectorName}",
                change.ConnectorName
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastNotificationCreatedAsync(string userId, InAppNotificationDto notification)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting notification created to user {UserId}: {NotificationId}",
                userId,
                notification.Id
            );

            var userGroup = $"user-{userId}";
            await _dataHubContext
                .Clients.Group(userGroup)
                .SendCoreAsync("notificationCreated", new object[] { notification });

            _logger.LogDebug("Notification created broadcast completed for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting notification created to user {UserId}",
                userId
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastNotificationArchivedAsync(string userId, InAppNotificationDto notification, NotificationArchiveReason archiveReason)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting notification archived to user {UserId}: {NotificationId}, reason: {Reason}",
                userId,
                notification.Id,
                archiveReason
            );

            var userGroup = $"user-{userId}";
            var payload = new { notification, archiveReason };
            await _dataHubContext
                .Clients.Group(userGroup)
                .SendCoreAsync("notificationArchived", new object[] { payload });

            _logger.LogDebug("Notification archived broadcast completed for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting notification archived to user {UserId}",
                userId
            );
        }
    }

    /// <inheritdoc />
    public async Task BroadcastNotificationUpdatedAsync(string userId, InAppNotificationDto notification)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting notification updated to user {UserId}: {NotificationId}",
                userId,
                notification.Id
            );

            var userGroup = $"user-{userId}";
            await _dataHubContext
                .Clients.Group(userGroup)
                .SendCoreAsync("notificationUpdated", new object[] { notification });

            _logger.LogDebug("Notification updated broadcast completed for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting notification updated to user {UserId}",
                userId
            );
        }
    }
}
