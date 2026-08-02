using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for alarm notifications, replacing socket.io alarm namespace
/// </summary>
// The handshake is anonymous because clients authenticate in-band, after negotiate: a client that
// can only present its credential once the connection is up would otherwise be refused before it
// could. The hub endpoint is internet-reachable (the cloud gateway publishes /hubs/**), so
// authorization happens per method in HubAuthorizationFilter, not at the handshake.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public class AlarmHub : TenantAwareHub
{
    private readonly ILogger<AlarmHub> _logger;
    private readonly IHubTokenAuthorizer _tokenAuthorizer;

    public AlarmHub(ILogger<AlarmHub> logger, IHubTokenAuthorizer tokenAuthorizer)
    {
        _logger = logger;
        _tokenAuthorizer = tokenAuthorizer;
    }

    /// <summary>
    /// Subscribe to alarm notifications (replaces socket.io 'subscribe' event). Legacy alarms carry
    /// the glucose reading that triggered them, so glucose read is the gate.
    /// </summary>
    /// <param name="authData">Authorization data containing secret and JWT token</param>
    /// <returns>Subscription result</returns>
    [HubAuthenticationMethod]
    [HubTenantGroup]
    public async Task<object> Subscribe(AlarmSubscribeRequest authData)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} subscribing to alarms",
                Context.ConnectionId
            );

            // A connection that presented a credential on the HTTP upgrade is already authenticated
            // and scoped; otherwise authenticate from the in-band payload. Both credential shapes go
            // through IHubTokenAuthorizer so the tenant pin and scope check match DataHub's.
            var authorization = HubAuthorizationState.Resolve(Context);

            if (authorization is null && !string.IsNullOrEmpty(authData.JwtToken))
            {
                authorization = await _tokenAuthorizer.AuthorizeTokenAsync(
                    authData.JwtToken,
                    TenantContext?.TenantId,
                    OAuthScopes.GlucoseRead
                );
            }
            else if (authorization is null && !string.IsNullOrEmpty(authData.Secret))
            {
                authorization = _tokenAuthorizer.AuthorizeInstanceKey(
                    authData.Secret,
                    TenantContext?.TenantId
                );
            }

            // The group carries the tenant's alarms, announcements and notifications, not one data
            // category, so a share-style grant is refused here as it is on DataHub's tenant groups.
            // [HubTenantGroup] declares that requirement; HubAuthorizationFilter cannot enforce it on
            // an authentication entry point, because the credential arrives in this invocation.
            if (authorization is null
                || !authorization.CanJoinTenantRelay
                || !authorization.Satisfies(OAuthScopes.GlucoseRead))
            {
                _logger.LogWarning(
                    "Client {ConnectionId} alarm subscription failed - unauthorized",
                    Context.ConnectionId
                );
                return new { read = false, success = false };
            }

            HubAuthorizationState.Grant(Context, authorization);

            // Add connection to tenant-scoped alarm subscribers group
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                TenantGroup("alarm-subscribers")
            );

            _logger.LogInformation(
                "Client {ConnectionId} subscribed to alarms successfully",
                Context.ConnectionId
            );

            return new { read = true, success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error during alarm subscription for client {ConnectionId}",
                Context.ConnectionId
            );
            return new
            {
                read = false,
                success = false,
                error = "Subscription failed",
            };
        }
    }

    /// <summary>
    /// Acknowledge alarms from clients (replaces socket.io 'ack' event)
    /// </summary>
    /// <param name="level">Alarm level to acknowledge</param>
    /// <param name="group">Alarm group to acknowledge</param>
    /// <param name="silenceTime">Time to silence alarm in milliseconds</param>
    [HubScope(OAuthScopes.AlertsReadWrite)]
    public async Task Ack(int level, string group, int silenceTime)
    {
        try
        {
            _logger.LogInformation(
                "Alarm ack received: level={Level}, group={Group}, silenceTime={SilenceTime}",
                level,
                group,
                silenceTime
            );

            // Get notification service from DI container
            var serviceProvider = Context.GetHttpContext()?.RequestServices;
            var notificationV1Service = serviceProvider?.GetService<INotificationV1Service>();

            if (notificationV1Service == null)
            {
                _logger.LogWarning("NotificationV1Service not available for alarm acknowledgment");
                return;
            }

            // Create acknowledgment request
            var ackRequest = new NotificationAckRequest
            {
                Level = level,
                Group = group,
                Time = silenceTime,
                SendClear = true, // Send clear notification after acknowledgment
            };

            // Process the acknowledgment through the notification service
            var ackResult = await notificationV1Service.AckNotificationAsync(ackRequest);

            if (ackResult.Success)
            {
                _logger.LogInformation(
                    "Successfully acknowledged alarm via SignalR - level: {Level}, group: {Group}",
                    level,
                    group
                );

                // Send acknowledgment confirmation back to the client
                await Clients.Caller.SendAsync(
                    "ackConfirm",
                    new
                    {
                        success = true,
                        level = level,
                        group = group,
                        silenceTime = silenceTime,
                        message = ackResult.Message,
                        timestamp = ackResult.Timestamp,
                    }
                );

                // Broadcast to all tenant alarm subscribers that this alarm was acknowledged
                await Clients
                    .Group(TenantGroup("alarm-subscribers"))
                    .SendAsync(
                        "alarmAck",
                        new
                        {
                            level = level,
                            group = group,
                            silenceTime = silenceTime,
                            timestamp = ackResult.Timestamp,
                        }
                    );
            }
            else
            {
                _logger.LogWarning(
                    "Failed to acknowledge alarm via SignalR - level: {Level}, group: {Group}, error: {Error}",
                    level,
                    group,
                    ackResult.Message
                );

                // Send error response back to the client
                await Clients.Caller.SendAsync(
                    "ackConfirm",
                    new
                    {
                        success = false,
                        level = level,
                        group = group,
                        message = ackResult.Message,
                        timestamp = ackResult.Timestamp,
                    }
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing alarm acknowledgment");

            // Send error response back to the client
            await Clients.Caller.SendAsync(
                "ackConfirm",
                new
                {
                    success = false,
                    level = level,
                    group = group,
                    message = "Internal error processing acknowledgment",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    public override async Task OnConnectedAsync()
    {
        // base.OnConnectedAsync() validates tenant context from the HTTP upgrade handshake
        await base.OnConnectedAsync();
        _logger.LogInformation(
            "Client {ConnectionId} connected to AlarmHub for tenant {TenantSlug}",
            Context.ConnectionId,
            TenantContext?.Slug
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Client {ConnectionId} disconnected from AlarmHub",
            Context.ConnectionId
        );
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Alarm subscription request model
/// </summary>
public class AlarmSubscribeRequest
{
    public string? Secret { get; set; }
    public string? JwtToken { get; set; }
}
