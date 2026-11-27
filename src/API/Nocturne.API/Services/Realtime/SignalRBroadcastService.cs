using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Monitoring;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Service for broadcasting real-time updates via SignalR, replacing the legacy socket.io server-side broadcasting.
/// All broadcasts are scoped to the current tenant's SignalR groups to enforce isolation.
/// </summary>
/// <remarks>
/// Every method logs its own failure at error and returns, so a caller does not wrap it in a try. A
/// broadcast announces a write that has already committed, so a hub or backplane outage, and a defect
/// in the broadcast itself, must not fail the caller that wrote.
/// </remarks>
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

    /// <summary>
    /// Broadcast an alert engine alarm or all-clear to alarm subscribers, as one of the
    /// <see cref="Alerts.EngineAlarmEvents"/>.
    /// </summary>
    Task BroadcastEngineAlarmAsync(string eventName, NotificationBase notify);

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
    /// Broadcast a tracker instance update to the tenant's authorized clients, as a
    /// <c>trackerUpdate</c> event.
    /// </summary>
    Task BroadcastTrackerUpdateAsync(TrackerRealtimeAction action, TrackerInstanceDto trackerInstance);

    /// <summary>
    /// Tell the tenant's authorized clients that a tracker definition was created, changed or
    /// deleted, as a <c>trackerDefinitionUpdate</c> event carrying its id.
    /// </summary>
    Task BroadcastTrackerDefinitionUpdateAsync(Guid definitionId);

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
    /// Broadcast an alert engine event to the tenant's alert subscribers group only. Resolutions and
    /// acknowledgements go through <see cref="BroadcastAlertResolvedAsync"/> and
    /// <see cref="BroadcastAlertAcknowledgedAsync"/>, which also reach Home Assistant.
    /// </summary>
    /// <param name="eventName">The event name to broadcast</param>
    /// <param name="payload">The event payload</param>
    Task BroadcastAlertEventAsync(string eventName, object payload);

    /// <summary>
    /// Broadcast <c>alert_resolved</c> to the tenant's alert subscribers and Home Assistant instances.
    /// </summary>
    Task BroadcastAlertResolvedAsync(AlertResolvedEvent resolved);

    /// <summary>
    /// Send <c>alert_resolved</c> only to the named Home Assistant instances (OAuth client ids), for
    /// an event that reached those instances alone.
    /// </summary>
    Task BroadcastAlertResolvedToHomeAssistantAsync(AlertResolvedEvent resolved, IReadOnlyCollection<string> instances);

    /// <summary>
    /// Broadcast <c>alert_acknowledged</c> to the tenant's alert subscribers and Home Assistant instances.
    /// </summary>
    Task BroadcastAlertAcknowledgedAsync(AlertAcknowledgedEvent acknowledged);

    /// <summary>
    /// Broadcast a device actuation intent to the tenant's authenticated DataHub clients.
    /// Registered push-mode devices (e.g. the Companion) act on it when it targets their kind;
    /// other clients ignore it. Tenant-scoped and authenticated (not the anonymous AlertHub).
    /// </summary>
    /// <param name="intent">The actuation intent to deliver.</param>
    Task BroadcastDeviceActionAsync(DeviceActionIntent intent);

    /// <summary>
    /// Send a device actuation intent to one subject's DataHub connections only, for a change
    /// that concerns that member alone, such as muting an excursion for themselves.
    /// </summary>
    /// <param name="subjectId">The subject whose registered devices should reconcile.</param>
    /// <param name="intent">The actuation intent to deliver.</param>
    Task BroadcastDeviceActionToSubjectAsync(Guid subjectId, DeviceActionIntent intent);

    /// <summary>
    /// Mirror a non-alert in-app notification to the tenant's authenticated clients. A device
    /// surfaces it only if it owns the notification (matches <see cref="DeviceNotificationMirror.UserId"/>);
    /// web clients ignore it. Alerts do NOT use this path (they go via device_action).
    /// </summary>
    /// <param name="mirror">The user-tagged notification to mirror.</param>
    Task BroadcastDeviceNotificationAsync(DeviceNotificationMirror mirror);

    /// <summary>
    /// Broadcast sync progress event to subscribers via ConfigHub
    /// </summary>
    Task BroadcastSyncProgressAsync(SyncProgressEvent progress);

    /// <summary>
    /// Broadcast <c>glucose_reading</c> to the tenant's Home Assistant instances that may read glucose.
    /// </summary>
    Task BroadcastHomeAssistantGlucoseAsync(GlucoseReadingEvent reading);
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
/// to prevent cross-tenant data leakage.
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
    private readonly IHubContext<OverviewHub> _overviewHubContext;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IOptions<JsonHubProtocolOptions> _hubProtocolOptions;
    private readonly ILogger<SignalRBroadcastService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRBroadcastService"/>.
    /// </summary>
    /// <param name="dataHubContext">Hub context for <see cref="DataHub"/> — data updates and storage events.</param>
    /// <param name="alarmHubContext">Hub context for <see cref="AlarmHub"/> — notifications, alarms, and announcements.</param>
    /// <param name="configHubContext">Hub context for <see cref="ConfigHub"/> — configuration changes and sync progress.</param>
    /// <param name="alertHubContext">Hub context for <see cref="AlertHub"/> — alert engine dispatch, resolution, and acknowledgement events.</param>
    /// <param name="homeAssistantHubContext">Hub context for <see cref="HomeAssistantHub"/> — glucose relay and alert event relay to Home Assistant instances.</param>
    /// <param name="overviewHubContext">Hub context for <see cref="OverviewHub"/> — cross-tenant overview pings.</param>
    /// <param name="tenantAccessor">Provides the current tenant context for scoping group names.</param>
    /// <param name="hubProtocolOptions">The serializer the hubs write payloads with — see <see cref="WithWireIdentifier"/>.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalRBroadcastService(
        IHubContext<DataHub> dataHubContext,
        IHubContext<AlarmHub> alarmHubContext,
        IHubContext<ConfigHub> configHubContext,
        IHubContext<AlertHub> alertHubContext,
        IHubContext<HomeAssistantHub> homeAssistantHubContext,
        IHubContext<OverviewHub> overviewHubContext,
        ITenantAccessor tenantAccessor,
        IOptions<JsonHubProtocolOptions> hubProtocolOptions,
        ILogger<SignalRBroadcastService> logger
    )
    {
        _dataHubContext = dataHubContext;
        _alarmHubContext = alarmHubContext;
        _configHubContext = configHubContext;
        _alertHubContext = alertHubContext;
        _homeAssistantHubContext = homeAssistantHubContext;
        _overviewHubContext = overviewHubContext;
        _tenantAccessor = tenantAccessor;
        _hubProtocolOptions = hubProtocolOptions;
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

    /// <summary>Isolates one broadcast, for the reason in the remarks of <see cref="ISignalRBroadcastService"/>.</summary>
    private async Task IsolatedAsync(Func<Task> broadcast, string failure, params object?[] args)
    {
        try
        {
            await broadcast();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, failure, args);
        }
    }

    /// <inheritdoc />
    public async Task BroadcastDataUpdateAsync(object data)
    {
        await IsolatedAsync(async () =>
        {
            var group = TenantGroup(RealtimeGroups.Authorized);
            _logger.LogInformation(
                "Broadcasting data update to {Group}: {DataType}",
                group,
                data.GetType().Name
            );
            await _dataHubContext
                .Clients.Group(group)
                .SendCoreAsync("dataUpdate", new[] { data });
            _logger.LogInformation("Data update broadcast completed successfully");
        }, "Error broadcasting data update");

        // Tenant-tagged ping to cross-tenant overview subscribers. Same "{tenantId}:" group
        // scheme but on OverviewHub, so no collision with DataHub groups. Minimal payload:
        // clients refetch the overview endpoint.
        await IsolatedAsync(async () =>
        {
            var tenantId = GetTenantId();
            await _overviewHubContext
                .Clients.Group(TenantAwareHub.FormatTenantGroup(tenantId, OverviewHub.GroupName))
                .SendCoreAsync("overviewUpdate", new object[] { new { tenantId } });
        }, "Error broadcasting overview update");
    }

    /// <inheritdoc />
    public Task BroadcastRetroUpdateAsync(string connectionId, object retroData) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting retro update to client {ConnectionId}", connectionId);
            await _dataHubContext
                .Clients.Client(connectionId)
                .SendCoreAsync("retroUpdate", new[] { retroData });
        }, "Error broadcasting retro update to client {ConnectionId}", connectionId);

    /// <inheritdoc />
    public Task BroadcastNotificationAsync(NotificationBase notification) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting notification: {Title}", notification.Title);
            await _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync("notification", new[] { notification });
        }, "Error broadcasting notification");

    /// <inheritdoc />
    public Task BroadcastAnnouncementAsync(NotificationBase announcement) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting announcement: {Title}", announcement.Title);
            await _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync("announcement", new[] { announcement });
        }, "Error broadcasting announcement");

    /// <inheritdoc />
    public Task BroadcastAlarmAsync(NotificationBase alarm) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting alarm: {Title}", alarm.Title);
            await _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync("alarm", new[] { alarm });
        }, "Error broadcasting alarm");

    /// <inheritdoc />
    public Task BroadcastUrgentAlarmAsync(NotificationBase urgentAlarm) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting urgent alarm: {Title}", urgentAlarm.Title);
            await _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync("urgent_alarm", new[] { urgentAlarm });
        }, "Error broadcasting urgent alarm");

    /// <inheritdoc />
    public Task BroadcastClearAlarmAsync(NotificationBase clearAlarm) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting clear alarm: {Title}", clearAlarm.Title);
            await _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync("clear_alarm", new[] { clearAlarm });
        }, "Error broadcasting clear alarm");

    /// <inheritdoc />
    public Task BroadcastEngineAlarmAsync(string eventName, NotificationBase notify) =>
        IsolatedAsync(
            () => _alarmHubContext
                .Clients.Group(TenantGroup("alarm-subscribers"))
                .SendCoreAsync(eventName, new[] { notify }),
            "Error broadcasting {EventName}", eventName);

    /// <inheritdoc />
    public Task BroadcastStorageCreateAsync(string collectionName, object data) =>
        IsolatedAsync(
            () => _dataHubContext
                .Clients.Group(TenantGroup(collectionName))
                .SendCoreAsync("create", new[] { WithWireDocument(data) }),
            "Error broadcasting storage create event for collection {Collection}", collectionName);

    /// <inheritdoc />
    public Task BroadcastStorageUpdateAsync(string collectionName, object data) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting storage update event for collection {Collection}",
                collectionName
            );
            await _dataHubContext
                .Clients.Group(TenantGroup(collectionName))
                .SendCoreAsync("update", new[] { WithWireDocument(data) });
        }, "Error broadcasting storage update event for collection {Collection}", collectionName);

    /// <inheritdoc />
    public Task BroadcastStorageDeleteAsync(string collectionName, object data) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting storage delete event for collection {Collection}",
                collectionName
            );
            await _dataHubContext
                .Clients.Group(TenantGroup(collectionName))
                .SendCoreAsync("delete", new[] { WithWireIdentifier(data) });
        }, "Error broadcasting storage delete event for collection {Collection}", collectionName);

    /// <summary>
    /// Resolves a <see cref="StorageDocumentEvent"/> against the payload serializer this hub sends
    /// with. Any other payload passes through.
    /// </summary>
    private object WithWireDocument(object data) =>
        data is StorageDocumentEvent document
            ? document.OnWire(_hubProtocolOptions.Value.PayloadSerializerOptions)
            : data;

    /// <summary>
    /// Resolves a <see cref="StorageDeleteEvent"/> against the payload serializer this hub sends with,
    /// which is the only serializer whose output a client ever sees. Any other payload passes through.
    /// </summary>
    private object WithWireIdentifier(object data)
    {
        if (data is not StorageDeleteEvent delete)
            return data;

        var resolved = delete.OnWire(_hubProtocolOptions.Value.PayloadSerializerOptions);

        if (resolved.Identifier is null)
            _logger.LogWarning(
                "Storage delete for {Collection} carries no identifier; no client can resolve it",
                resolved.ColName
            );

        return resolved;
    }

    /// <inheritdoc />
    public Task BroadcastTrackerUpdateAsync(TrackerRealtimeAction action, TrackerInstanceDto trackerInstance) =>
        IsolatedAsync(async () =>
        {
            _logger.LogInformation(
                "Broadcasting tracker update event: {Action}",
                action
            );
            var payload = new { action, instance = trackerInstance };
            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Authorized))
                .SendCoreAsync("trackerUpdate", new[] { payload });
            _logger.LogDebug("Tracker update broadcast completed for action {Action}", action);
        }, "Error broadcasting tracker update event");

    /// <inheritdoc />
    public Task BroadcastTrackerDefinitionUpdateAsync(Guid definitionId) =>
        IsolatedAsync(
            () => _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Authorized))
                .SendCoreAsync("trackerDefinitionUpdate", new object[] { new { definitionId } }),
            "Error broadcasting tracker definition update event");

    /// <inheritdoc />
    public Task BroadcastConfigChangeAsync(ConfigurationChangeEvent change) =>
        IsolatedAsync(async () =>
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
        }, "Error broadcasting config change for {ConnectorName}", change.ConnectorName);

    /// <inheritdoc />
    public Task BroadcastNotificationCreatedAsync(string userId, InAppNotificationDto notification) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting notification created to user {UserId}: {NotificationId}",
                userId,
                notification.Id
            );

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            var userGroup = RealtimeGroups.ForSubject(userId);
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationCreated", new object[] { notification });

            // The bridge's copy, naming the recipient. See RealtimeGroups.Relay.
            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Relay))
                .SendCoreAsync(
                    "notificationCreated",
                    new object[] { notification, RealtimeGroups.NormalizeSubjectId(userId) });

            _logger.LogDebug("Notification created broadcast completed for user {UserId}", userId);
        }, "Error broadcasting notification created to user {UserId}", userId);

    /// <inheritdoc />
    public Task BroadcastNotificationArchivedAsync(string userId, InAppNotificationDto notification, NotificationArchiveReason archiveReason) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting notification archived to user {UserId}: {NotificationId}, reason: {Reason}",
                userId,
                notification.Id,
                archiveReason
            );

            var userGroup = RealtimeGroups.ForSubject(userId);
            var payload = new { notification, archiveReason };

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationArchived", new object[] { payload });

            // The bridge's copy, naming the recipient. See RealtimeGroups.Relay.
            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Relay))
                .SendCoreAsync(
                    "notificationArchived",
                    new object[] { payload, RealtimeGroups.NormalizeSubjectId(userId) });

            _logger.LogDebug("Notification archived broadcast completed for user {UserId}", userId);
        }, "Error broadcasting notification archived to user {UserId}", userId);

    /// <inheritdoc />
    public Task BroadcastNotificationUpdatedAsync(string userId, InAppNotificationDto notification) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting notification updated to user {UserId}: {NotificationId}",
                userId,
                notification.Id
            );

            var userGroup = RealtimeGroups.ForSubject(userId);

            // Broadcast to tenant-scoped user-specific group for multi-user scenarios
            await _dataHubContext
                .Clients.Group(TenantGroup(userGroup))
                .SendCoreAsync("notificationUpdated", new object[] { notification });

            // The bridge's copy, naming the recipient. See RealtimeGroups.Relay.
            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Relay))
                .SendCoreAsync(
                    "notificationUpdated",
                    new object[] { notification, RealtimeGroups.NormalizeSubjectId(userId) });

            _logger.LogDebug("Notification updated broadcast completed for user {UserId}", userId);
        }, "Error broadcasting notification updated to user {UserId}", userId);

    /// <inheritdoc />
    public Task BroadcastAlertEventAsync(string eventName, object payload) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting alert event {EventName}", eventName);
            await _alertHubContext
                .Clients.Group(TenantGroup("alert-subscribers"))
                .SendCoreAsync(eventName, new[] { payload });
        }, "Error broadcasting alert event {EventName}", eventName);

    /// <inheritdoc />
    public Task BroadcastAlertResolvedAsync(AlertResolvedEvent resolved) =>
        BroadcastAlertEventWithHomeAssistantAsync("alert_resolved", resolved);

    /// <inheritdoc />
    public Task BroadcastAlertResolvedToHomeAssistantAsync(
        AlertResolvedEvent resolved, IReadOnlyCollection<string> instances) =>
        instances.Count == 0
            ? Task.CompletedTask
            : IsolatedAsync(
                () => _homeAssistantHubContext
                    .Clients.Groups([.. instances.Select(instance => TenantGroup($"ha:{instance}"))])
                    .SendCoreAsync("alert_resolved", new object[] { resolved }),
                "Error relaying alert_resolved to {Count} HA instances", instances.Count);

    /// <inheritdoc />
    public Task BroadcastAlertAcknowledgedAsync(AlertAcknowledgedEvent acknowledged) =>
        BroadcastAlertEventWithHomeAssistantAsync("alert_acknowledged", acknowledged);

    // alert_dispatch does not come through here: an instance receives it once, on its ha:{instance}
    // group, from HomeAssistantProvider, which also records the delivery.
    private async Task BroadcastAlertEventWithHomeAssistantAsync(string eventName, object payload)
    {
        await BroadcastAlertEventAsync(eventName, payload);

        await IsolatedAsync(
            () => _homeAssistantHubContext
                .Clients.Group(TenantGroup("ha-alerts"))
                .SendCoreAsync(eventName, new[] { payload }),
            "Error relaying alert event {EventName} to HA", eventName);
    }

    /// <inheritdoc />
    public Task BroadcastDeviceActionAsync(DeviceActionIntent intent) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting device_action {Intent} for excursion {ExcursionId} (kind {Kind})",
                intent.Intent, intent.ExcursionId, intent.TargetKind);

            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Authorized))
                .SendCoreAsync("device_action", new object[] { intent });
        }, "Error broadcasting device_action for excursion {ExcursionId}", intent.ExcursionId);

    /// <inheritdoc />
    public Task BroadcastDeviceActionToSubjectAsync(Guid subjectId, DeviceActionIntent intent) =>
        IsolatedAsync(
            () => _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.ForSubject(subjectId)))
                .SendCoreAsync("device_action", new object[] { intent }),
            "Error sending device_action for excursion {ExcursionId} to subject {SubjectId}",
            intent.ExcursionId, subjectId);

    /// <inheritdoc />
    public Task BroadcastDeviceNotificationAsync(DeviceNotificationMirror mirror) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting device_notification mirror for user {UserId}: {NotificationId}",
                mirror.UserId, mirror.Notification.Id);

            // The mirror names the subject it belongs to, so it goes to that subject's group rather
            // than the tenant. A device is expected to filter to its owner, but relying on the
            // client to do so made the mirror readable by every other connection in the tenant.
            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.ForSubject(mirror.UserId)))
                .SendCoreAsync("device_notification", new object[] { mirror });

            await _dataHubContext
                .Clients.Group(TenantGroup(RealtimeGroups.Relay))
                .SendCoreAsync("device_notification", new object[] { mirror });
        }, "Error broadcasting device_notification for user {UserId}", mirror.UserId);

    /// <inheritdoc />
    public Task BroadcastHomeAssistantGlucoseAsync(GlucoseReadingEvent reading) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug("Broadcasting glucose_reading to HA subscribers");
            await _homeAssistantHubContext
                .Clients.Group(TenantGroup("ha-glucose"))
                .SendCoreAsync("glucose_reading", new object[] { reading });
        }, "Error broadcasting glucose_reading to HA");

    /// <inheritdoc />
    public Task BroadcastSyncProgressAsync(SyncProgressEvent progress) =>
        IsolatedAsync(async () =>
        {
            _logger.LogDebug(
                "Broadcasting sync progress for {ConnectorId}: {Phase}",
                progress.ConnectorId,
                progress.Phase
            );

            await _configHubContext
                .Clients.Group(TenantGroup("config:all"))
                .SendCoreAsync("syncProgress", [progress]);
        }, "Error broadcasting sync progress for {ConnectorId}", progress.ConnectorId);
}
