using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Hubs;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Service for broadcasting real-time updates via SignalR, replacing the legacy socket.io server-side broadcasting.
/// All broadcasts are scoped to the current tenant's SignalR groups to enforce isolation.
/// </summary>
/// <seealso cref="SignalRBroadcastService"/>
/// <seealso cref="DataHub"/>
/// <seealso cref="AlarmHub"/>
/// <seealso cref="ConfigHub"/>
/// <seealso cref="AlertHub"/>
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

    /// <summary>
    /// Broadcast an alert engine event (alert_dispatch, alert_resolved, alert_acknowledged)
    /// to the tenant's alert subscribers group.
    /// </summary>
    /// <param name="eventName">The event name to broadcast</param>
    /// <param name="payload">The event payload</param>
    Task BroadcastAlertEventAsync(string eventName, object payload);

    /// <summary>
    /// Broadcast sync progress event to subscribers via ConfigHub
    /// </summary>
    Task BroadcastSyncProgressAsync(SyncProgressEvent progress);

    /// <summary>
    /// Broadcast glucose reading to Home Assistant subscribers via HomeAssistantHub
    /// </summary>
    Task BroadcastHomeAssistantGlucoseAsync(object glucoseData);
}

/// <summary>
/// Concrete implementation of <see cref="ISignalRBroadcastService"/> that routes broadcasts
/// to tenant-scoped groups across five hubs: <see cref="DataHub"/> (data and storage events),
/// <see cref="AlarmHub"/> (notifications and alarms), <see cref="ConfigHub"/> (configuration
/// changes and sync progress), <see cref="AlertHub"/> (alert engine events), and
/// <see cref="HomeAssistantHub"/> (glucose relay and alert event relay to HA instances).
/// </summary>
/// <remarks>
/// Group names are always prefixed with the tenant ID via <see cref="TenantAwareHub.FormatTenantGroup"/>
/// to prevent cross-tenant data leakage. Broadcast failures are caught and logged; they never
/// propagate to the caller so that a SignalR outage cannot break write operations.
/// </remarks>
/// <seealso cref="ISignalRBroadcastService"/>
/// <seealso cref="DataHub"/>
/// <seealso cref="AlarmHub"/>
/// <seealso cref="ConfigHub"/>
/// <seealso cref="AlertHub"/>
/// <seealso cref="HomeAssistantHub"/>
public class SignalRBroadcastService : ISignalRBroadcastService
{
    private readonly IHubContext<DataHub> _dataHubContext;
    private readonly IHubContext<AlarmHub> _alarmHubContext;
    private readonly IHubContext<ConfigHub> _configHubContext;
    private readonly IHubContext<AlertHub> _alertHubContext;
    private readonly IHubContext<HomeAssistantHub> _homeAssistantHubContext;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<SignalRBroadcastService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRBroadcastService"/>.
    /// </summary>
    /// <param name="dataHubContext">Hub context for <see cref="DataHub"/> — data updates and storage events.</param>
    /// <param name="alarmHubContext">Hub context for <see cref="AlarmHub"/> — notifications, alarms, and announcements.</param>
    /// <param name="configHubContext">Hub context for <see cref="ConfigHub"/> — configuration changes and sync progress.</param>
    /// <param name="alertHubContext">Hub context for <see cref="AlertHub"/> — alert engine dispatch, resolution, and acknowledgement events.</param>
    /// <param name="homeAssistantHubContext">Hub context for <see cref="HomeAssistantHub"/> — glucose relay and alert event relay to Home Assistant instances.</param>
    /// <param name="tenantAccessor">Provides the current tenant context for scoping group names.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalRBroadcastService(
        IHubContext<DataHub> dataHubContext,
        IHubContext<AlarmHub> alarmHubContext,
        IHubContext<ConfigHub> configHubContext,
        IHubContext<AlertHub> alertHubContext,
        IHubContext<HomeAssistantHub> homeAssistantHubContext,
        ITenantAccessor tenantAccessor,
        ILogger<SignalRBroadcastService> logger
    )
    {
        _dataHubContext = dataHubContext;
        _alarmHubContext = alarmHubContext;
        _configHubContext = configHubContext;
        _alertHubContext = alertHubContext;
        _homeAssistantHubContext = homeAssistantHubContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current tenant ID (as a string) from the tenant accessor, or throws if not resolved.
    /// Uses the immutable TenantId (GUID) instead of the mutable Slug to prevent
    /// cache poisoning if a tenant's slug is changed.
    /// </summary>
    private string GetTenantId()
    {
        var tenantId = _tenantAccessor.Context?.TenantId.ToString()
            ?? throw new InvalidOperationException(
                "Cannot broadcast: tenant context is not resolved. " +
                "SignalRBroadcastService must be called from a request with an active tenant context.");
        return tenantId;
    }

    /// <summary>
    /// Creates a tenant-scoped group name, consistent with the hub-side TenantAwareHub.TenantGroup method.
    /// </summary>
    private string TenantGroup(string groupName)
        => TenantAwareHub.FormatTenantGroup(GetTenantId(), groupName);

    /// <inheritdoc />
    public async Task BroadcastDataUpdateAsync(object data)
    {
        try
        {
            var group = TenantGroup("authorized");
            _logger.LogInformation(
                "Broadcasting data update to {Group}: {DataType}",
                group,
                data?.GetType().Name ?? "null"
            );
            await _dataHubContext
                .Clients.Group(group)
                .SendCoreAsync("dataUpdate", new[] { data });
            _logger.LogInformation("Data update broadcast completed successfully");

            // Relay to Home Assistant subscribers
            await BroadcastHomeAssistantGlucoseAsync(data);
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
                .Clients.Group(TenantGroup("alarm-subscribers"))
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
                .Clients.Group(TenantGroup("alarm-subscribers"))
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
                .Clients.Group(TenantGroup("alarm-subscribers"))
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
                .Clients.Group(TenantGroup("alarm-subscribers"))
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
                .Clients.Group(TenantGroup("alarm-subscribers"))
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
                .Clients.Group(TenantGroup(collectionName))
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
                .Clients.Group(TenantGroup(collectionName))
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
                .Clients.Group(TenantGroup(collectionName))
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
                .Clients.Group(TenantGroup("authorized"))
                .SendCoreAsync("trackerUpdate", new[] { payload });
            _logger.LogDebug("Tracker update broadcast completed for action {Action}", action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting tracker update event");
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

            // Broadcast to tenant-scoped connector-specific group
            var connectorGroup = $"config:{change.ConnectorName.ToLowerInvariant()}";
            await _configHubContext
                .Clients.Group(TenantGroup(connectorGroup))
                .SendCoreAsync("configChanged", new[] { change });

            // Also broadcast to tenant-scoped "all" subscribers
            await _configHubContext
                .Clients.Group(TenantGroup("config:all"))
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

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            var userGroup = $"user-{userId}";
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationCreated", new object[] { notification });

            // Also broadcast to tenant-scoped authorized group for single-user deployments and bridge relay
            await _dataHubContext
                .Clients.Group(TenantGroup("authorized"))
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

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationArchived", new object[] { payload });

            // Also broadcast to tenant-scoped authorized group for single-user deployments and bridge relay
            await _dataHubContext
                .Clients.Group(TenantGroup("authorized"))
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

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationUpdated", new object[] { notification });

            // Also broadcast to tenant-scoped authorized group for single-user deployments and bridge relay
            await _dataHubContext
                .Clients.Group(TenantGroup("authorized"))
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

    /// <inheritdoc />
    public async Task BroadcastAlertEventAsync(string eventName, object payload)
    {
        try
        {
            _logger.LogDebug("Broadcasting alert event {EventName}", eventName);
            await _alertHubContext
                .Clients.Group(TenantGroup("alert-subscribers"))
                .SendCoreAsync(eventName, new[] { payload });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting alert event {EventName}", eventName);
        }

        // Also relay to Home Assistant subscribers
        try
        {
            await _homeAssistantHubContext
                .Clients.Group(TenantGroup("ha-alerts"))
                .SendCoreAsync(eventName, new[] { payload });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error relaying alert event {EventName} to HA", eventName);
        }
    }

    /// <inheritdoc />
    public async Task BroadcastHomeAssistantGlucoseAsync(object glucoseData)
    {
        try
        {
            _logger.LogDebug("Broadcasting glucose_reading to HA subscribers");
            await _homeAssistantHubContext
                .Clients.Group(TenantGroup("ha-glucose"))
                .SendCoreAsync("glucose_reading", new[] { glucoseData });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting glucose_reading to HA");
        }
    }

    /// <inheritdoc />
    public async Task BroadcastSyncProgressAsync(SyncProgressEvent progress)
    {
        try
        {
            _logger.LogDebug(
                "Broadcasting sync progress for {ConnectorId}: {Phase} - {DataType}",
                progress.ConnectorId,
                progress.Phase,
                progress.CurrentDataType
            );

            await _configHubContext
                .Clients.Group(TenantGroup("config:all"))
                .SendCoreAsync("syncProgress", [progress]);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error broadcasting sync progress for {ConnectorId}",
                progress.ConnectorId
            );
        }
    }
}
