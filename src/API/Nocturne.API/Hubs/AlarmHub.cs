using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Middleware;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for alarm notifications, replacing socket.io alarm namespace
/// </summary>
public class AlarmHub : TenantAwareHub
{
    private readonly ILogger<AlarmHub> _logger;
    private readonly IAuthorizationService _authorizationService;

    public AlarmHub(ILogger<AlarmHub> logger, IAuthorizationService authorizationService)
    {
        _logger = logger;
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// Subscribe to alarm notifications (replaces socket.io 'subscribe' event)
    /// </summary>
    /// <param name="authData">Authorization data containing secret and JWT token</param>
    /// <returns>Subscription result</returns>
    public async Task<object> Subscribe(AlarmSubscribeRequest authData)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} subscribing to alarms",
                Context.ConnectionId
            ); // Check authentication through existing middleware context
            var authContext =
                Context.GetHttpContext()?.Items["AuthContext"] as Middleware.AuthenticationContext;
            bool isAuthorized = authContext?.IsAuthenticated ?? false;

            // If not already authenticated through middleware, try to authenticate with provided credentials
            if (!isAuthorized)
            {
                if (!string.IsNullOrEmpty(authData.JwtToken))
                {
                    // Try to generate JWT from access token
                    var authResponse = await _authorizationService.GenerateJwtFromAccessTokenAsync(
                        authData.JwtToken
                    );
                    isAuthorized = authResponse != null;
                }
                else if (!string.IsNullOrEmpty(authData.Secret))
                {
                    // For API secret, we need to validate it against the configured secret
                    // This would normally be done by the authentication middleware
                    // For SignalR hubs, we need to implement the same logic
                    var configuration = Context
                        .GetHttpContext()
                        ?.RequestServices.GetRequiredService<IConfiguration>();
                    var configuredSecret =
                        configuration?[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
                        ?? configuration?[ServiceNames.ConfigKeys.InstanceKey];
                    if (!string.IsNullOrEmpty(configuredSecret))
                    {
                        // Calculate SHA1 hash of the configured secret
                        using var sha1 = System.Security.Cryptography.SHA1.Create();
                        var secretBytes = System.Text.Encoding.UTF8.GetBytes(configuredSecret);
                        var hashBytes = sha1.ComputeHash(secretBytes);
                        var expectedHash = BitConverter
                            .ToString(hashBytes)
                            .Replace("-", "")
                            .ToLowerInvariant();

                        // Compare with provided secret (should be the hashed value)
                        isAuthorized = authData.Secret.ToLowerInvariant() == expectedHash;
                    }
                }
            }

            if (isAuthorized)
            {
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
            else
            {
                _logger.LogWarning(
                    "Client {ConnectionId} alarm subscription failed - unauthorized",
                    Context.ConnectionId
                );
                return new { read = false, success = false };
            }
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
