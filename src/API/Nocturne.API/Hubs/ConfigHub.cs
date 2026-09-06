using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for real-time configuration change notifications.
/// Connectors can subscribe to receive notifications when their configuration changes.
/// </summary>
/// <remarks>
/// Every method requires <see cref="Scope.FullAccess"/>. Connector configuration is tenant
/// administration and the OAuth vocabulary has no narrower scope for it, and the broadcasts name the
/// member who made each change. The consumers are the realtime bridge (instance key) and the tenant
/// owner, both of which hold full access.
///
/// The two subscribe methods also carry <see cref="HubTenantGroupAttribute"/>: the config groups carry
/// the tenant's connector administration, not one data category.
/// </remarks>
[Authorize]
public class ConfigHub : TenantAwareHub
{
    private readonly ILogger<ConfigHub> _logger;

    public ConfigHub(ILogger<ConfigHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to configuration changes for a specific connector.
    /// </summary>
    /// <param name="connectorName">The connector name to subscribe to</param>
    [HubScope(Scope.FullAccess)]
    [HubTenantGroup]
    public async Task Subscribe(string connectorName)
    {
        _logger.LogDebug("Client {ConnectionId} subscribing to config changes for {ConnectorName}",
            Context.ConnectionId, connectorName);

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(GetConfigGroupName(connectorName)));
    }

    /// <summary>
    /// Unsubscribe from configuration changes for a specific connector.
    /// </summary>
    /// <param name="connectorName">The connector name to unsubscribe from</param>
    [HubScope(Scope.FullAccess)]
    public async Task Unsubscribe(string connectorName)
    {
        _logger.LogDebug("Client {ConnectionId} unsubscribing from config changes for {ConnectorName}",
            Context.ConnectionId, connectorName);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TenantGroup(GetConfigGroupName(connectorName)));
    }

    /// <summary>
    /// Subscribe to configuration changes for all connectors.
    /// </summary>
    [HubScope(Scope.FullAccess)]
    [HubTenantGroup]
    public async Task SubscribeAll()
    {
        _logger.LogDebug("Client {ConnectionId} subscribing to all config changes", Context.ConnectionId);

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("config:all"));
    }

    /// <summary>
    /// Unsubscribe from all connector configuration changes.
    /// </summary>
    [HubScope(Scope.FullAccess)]
    public async Task UnsubscribeAll()
    {
        _logger.LogDebug("Client {ConnectionId} unsubscribing from all config changes", Context.ConnectionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TenantGroup("config:all"));
    }

    public override async Task OnConnectedAsync()
    {
        // base.OnConnectedAsync() validates tenant context from the HTTP upgrade handshake
        await base.OnConnectedAsync();
        _logger.LogDebug(
            "ConfigHub client connected: {ConnectionId} for tenant {TenantSlug}",
            Context.ConnectionId,
            TenantContext?.Slug
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogWarning(exception, "ConfigHub client {ConnectionId} disconnected with error",
                Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("ConfigHub client disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private static string GetConfigGroupName(string connectorName) => $"config:{connectorName.ToLowerInvariant()}";
}

/// <summary>
/// Event sent when a connector's configuration changes.
/// </summary>
public class ConfigurationChangeEvent
{
    /// <summary>
    /// The connector whose configuration changed.
    /// </summary>
    public string ConnectorName { get; set; } = string.Empty;

    /// <summary>
    /// Type of change: "updated", "deleted", "enabled", "disabled".
    /// </summary>
    public string ChangeType { get; set; } = string.Empty;

    /// <summary>
    /// When the change occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Who made the change.
    /// </summary>
    public string? ModifiedBy { get; set; }
}
