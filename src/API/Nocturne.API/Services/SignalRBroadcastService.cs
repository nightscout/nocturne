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
}

/// <summary>
/// Implementation of SignalR broadcast service
/// </summary>
public class SignalRBroadcastService : ISignalRBroadcastService
{
    private readonly IHubContext<DataHub> _dataHubContext;
    private readonly IHubContext<AlarmHub> _alarmHubContext;
    private readonly ILogger<SignalRBroadcastService> _logger;

    public SignalRBroadcastService(
        IHubContext<DataHub> dataHubContext,
        IHubContext<AlarmHub> alarmHubContext,
        ILogger<SignalRBroadcastService> logger
    )
    {
        _dataHubContext = dataHubContext;
        _alarmHubContext = alarmHubContext;
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
            _logger.LogInformation(
                "Broadcasting storage create event for collection {Collection}: {DataType}",
                collectionName,
                data?.GetType().Name ?? "null"
            );
            await _dataHubContext
                .Clients.Group(collectionName)
                .SendCoreAsync("create", new[] { data });
            _logger.LogInformation(
                "Storage create event broadcast completed for collection {Collection}",
                collectionName
            );
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
}
