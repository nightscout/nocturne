using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for real-time data updates, replacing socket.io main data connection
/// </summary>
public class DataHub : Hub
{
    private readonly ILogger<DataHub> _logger;
    private readonly Nocturne.Core.Contracts.IAuthorizationService _authorizationService;

    public DataHub(
        ILogger<DataHub> logger,
        Nocturne.Core.Contracts.IAuthorizationService authorizationService
    )
    {
        _logger = logger;
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// Client authorization method (replaces socket.io 'authorize' event)
    /// </summary>
    /// <param name="authData">Authorization data containing client info, secret, token, and history</param>
    /// <returns>Authorization result</returns>
    public async Task<object> Authorize(AuthorizeRequest authData)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} requesting authorization",
                Context.ConnectionId
            );

            // Check authentication through existing middleware context
            var authContext =
                Context.GetHttpContext()?.Items["AuthContext"] as Middleware.AuthenticationContext;
            bool isAuthorized = authContext?.IsAuthenticated ?? false;

            // If not already authenticated through middleware, try to authenticate with provided credentials
            if (!isAuthorized)
            {
                if (!string.IsNullOrEmpty(authData.Token))
                {
                    // Try to generate JWT from access token
                    var authResponse = await _authorizationService.GenerateJwtFromAccessTokenAsync(
                        authData.Token
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
                    var configuredSecret = configuration?["Parameters:api-secret"];

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
                // Add connection to authorized group
                await Groups.AddToGroupAsync(Context.ConnectionId, "authorized");

                // If user is admin, also add to admin group for admin-specific notifications
                var httpContext = Context.GetHttpContext();
                if (httpContext?.IsAdmin() == true)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
                    _logger.LogDebug(
                        "Client {ConnectionId} added to admin group",
                        Context.ConnectionId
                    );
                }

                _logger.LogInformation(
                    "Client {ConnectionId} authorized successfully",
                    Context.ConnectionId
                );

                return new
                {
                    read = true,
                    write = true,
                    success = true,
                };
            }
            else
            {
                _logger.LogWarning(
                    "Client {ConnectionId} authorization failed",
                    Context.ConnectionId
                );
                return new
                {
                    read = false,
                    write = false,
                    success = false,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error during authorization for client {ConnectionId}",
                Context.ConnectionId
            );
            return new
            {
                read = false,
                write = false,
                success = false,
                error = "Authorization failed",
            };
        }
    }

    /// <summary>
    /// Request retro data load (replaces socket.io 'loadRetro' event)
    /// </summary>
    /// <param name="request">Retro load request containing loadedMills timestamp</param>
    public async Task LoadRetro(RetroLoadRequest request)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} requesting retro data load from {LoadedMills}",
                Context.ConnectionId,
                request.LoadedMills
            );

            // Get services from DI container
            var serviceProvider = Context.GetHttpContext()?.RequestServices;
            var entryService = serviceProvider?.GetService<IEntryService>();
            var treatmentService = serviceProvider?.GetService<ITreatmentService>();
            var deviceStatusService = serviceProvider?.GetService<IDeviceStatusService>();

            if (entryService == null || treatmentService == null || deviceStatusService == null)
            {
                _logger.LogWarning("Required services not available for retro data loading");
                await Clients.Caller.SendAsync(
                    "retroUpdate",
                    new
                    {
                        error = "Services unavailable",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
                return;
            }

            // Calculate time range for retro data (typically last 24-48 hours from loadedMills)
            var endTime = request.LoadedMills;
            var startTime = endTime - (48 * 60 * 60 * 1000); // 48 hours before

            // Load retro data from multiple collections
            var entries = await entryService.GetEntriesAsync(
                find: $"{{\"mills\": {{\"$gte\": {startTime}, \"$lt\": {endTime}}}}}",
                count: 1000
            );

            var treatments = await treatmentService.GetTreatmentsAsync(
                find: $"{{\"mills\": {{\"$gte\": {startTime}, \"$lt\": {endTime}}}}}",
                count: 1000
            );

            var deviceStatuses = await deviceStatusService.GetDeviceStatusAsync(
                find: $"{{\"mills\": {{\"$gte\": {startTime}, \"$lt\": {endTime}}}}}",
                count: 1000
            );

            var retroData = new
            {
                entries = entries.ToArray(),
                treatments = treatments.ToArray(),
                devicestatus = deviceStatuses.ToArray(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                range = new { start = startTime, end = endTime },
            };

            // Send retro data to the requesting client
            await Clients.Caller.SendAsync("retroUpdate", retroData);

            _logger.LogDebug(
                "Sent retro data to client {ConnectionId}: {EntryCount} entries, {TreatmentCount} treatments, {DeviceStatusCount} device statuses",
                Context.ConnectionId,
                entries.Count(),
                treatments.Count(),
                deviceStatuses.Count()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error loading retro data for client {ConnectionId}",
                Context.ConnectionId
            );

            await Clients.Caller.SendAsync(
                "retroUpdate",
                new
                {
                    error = "Failed to load retro data",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Subscribe to storage collections (replaces socket.io '/storage' namespace 'subscribe' event)
    /// </summary>
    /// <param name="request">Storage subscription request</param>
    /// <returns>Subscription result</returns>
    public async Task<object> Subscribe(StorageSubscribeRequest request)
    {
        try
        {
            var enabledCollections = new[] { "entries", "treatments", "devicestatus", "profiles" };
            var collections = request.Collections ?? enabledCollections;
            var subscribed = new List<string>();

            foreach (var collection in collections)
            {
                if (enabledCollections.Contains(collection))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, collection);
                    subscribed.Add(collection);
                    _logger.LogDebug(
                        "Client {ConnectionId} subscribed to collection {Collection}",
                        Context.ConnectionId,
                        collection
                    );
                }
            }

            return new { success = true, collections = subscribed };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in storage subscription for client {ConnectionId}",
                Context.ConnectionId
            );
            return new { success = false, message = "Subscription failed" };
        }
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected to DataHub", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Client {ConnectionId} disconnected from DataHub",
            Context.ConnectionId
        );
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Authorization request model (replaces socket.io authorize event data)
/// </summary>
public class AuthorizeRequest
{
    public string? Client { get; set; }
    public string? Secret { get; set; }
    public string? Token { get; set; }
    public int History { get; set; }
}

/// <summary>
/// Retro load request model
/// </summary>
public class RetroLoadRequest
{
    public long LoadedMills { get; set; }
}

/// <summary>
/// Storage subscription request model (replaces socket.io storage namespace subscribe event data)
/// </summary>
public class StorageSubscribeRequest
{
    public string[]? Collections { get; set; }
    public string? AccessToken { get; set; }
}
