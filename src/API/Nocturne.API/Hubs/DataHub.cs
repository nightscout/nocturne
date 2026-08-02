using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for real-time data updates, replacing socket.io main data connection
/// </summary>
// The handshake is anonymous because clients authenticate in-band, after negotiate: a client that
// can only present its credential once the connection is up would otherwise be refused before it
// could. The hub endpoint is internet-reachable (the cloud gateway publishes /hubs/**), so
// authorization happens per method in HubAuthorizationFilter, not at the handshake.
[AllowAnonymous]
public class DataHub : TenantAwareHub
{
    private readonly ILogger<DataHub> _logger;
    private readonly IHubTokenAuthorizer _tokenAuthorizer;

    public DataHub(ILogger<DataHub> logger, IHubTokenAuthorizer tokenAuthorizer)
    {
        _logger = logger;
        _tokenAuthorizer = tokenAuthorizer;
    }

    /// <summary>
    /// Client authorization method (replaces socket.io 'authorize' event). Joins the tenant-scoped
    /// authorized group, which receives the tenant's live data broadcasts, and records the
    /// connection's credential for every subsequent hub method.
    /// </summary>
    /// <param name="authData">Authorization data containing client info, secret, token, and history</param>
    /// <returns>Authorization result</returns>
    [HubAuthenticationMethod]
    [HubTenantGroup]
    public async Task<object> Authorize(AuthorizeRequest authData)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} requesting authorization",
                Context.ConnectionId
            );

            // A connection that presented a credential on the HTTP upgrade is already authenticated
            // and scoped; otherwise authenticate from the in-band payload.
            var authorization = HubAuthorizationState.Resolve(Context);

            if (authorization is null && !string.IsNullOrEmpty(authData.Token))
            {
                // OAuth JWT (validated + tenant-pinned + scope-checked) or legacy opaque access
                // token. Glucose read is the gate: the authorized group receives the tenant's live
                // data broadcasts.
                authorization = await _tokenAuthorizer.AuthorizeTokenAsync(
                    authData.Token,
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

            if (authorization is null || !authorization.Satisfies(OAuthScopes.GlucoseRead))
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

            HubAuthorizationState.Grant(Context, authorization);

            // The tenant-wide groups carry more than the glucose read this method gates on —
            // tracker state, device action intents, arbitrary dataUpdate payloads — so only a
            // credential that belongs to the tenant joins them. [HubTenantGroup] declares that
            // requirement; HubAuthorizationFilter cannot enforce it on an authentication entry
            // point, because the credential arrives in this invocation, so the check is here. A
            // guest is not refused outright: it still authorizes, and joins nothing tenant-wide,
            // reaching the categories it was shared through Subscribe.
            if (authorization.CanJoinTenantRelay)
            {
                await Groups.AddToGroupAsync(
                    Context.ConnectionId, TenantGroup(RealtimeGroups.Authorized));

                // Per-subject payloads (in-app notifications, device notification mirrors) go to the
                // owning subject's group, so one member's notifications never reach another's client.
                if (authorization.OwnSubjectId is { } subjectId)
                {
                    await Groups.AddToGroupAsync(
                        Context.ConnectionId, TenantGroup(RealtimeGroups.ForSubject(subjectId)));
                }

                // The bridge consumes no payload itself; it relays every subject's to its own
                // clients, so it takes the tenant-wide copy of the per-subject broadcasts.
                if (authorization.IsInfrastructure)
                {
                    await Groups.AddToGroupAsync(
                        Context.ConnectionId, TenantGroup(RealtimeGroups.Relay));
                }

                // If user is admin, also add to admin group for admin-specific notifications
                var httpContext = Context.GetHttpContext();
                if (httpContext?.IsAdmin() == true)
                {
                    await Groups.AddToGroupAsync(
                        Context.ConnectionId, TenantGroup(RealtimeGroups.Admin));
                    _logger.LogDebug(
                        "Client {ConnectionId} added to admin group",
                        Context.ConnectionId
                    );
                }
            }

            _logger.LogInformation(
                "Client {ConnectionId} authorized successfully ({Kind})",
                Context.ConnectionId,
                authorization.Kind
            );

            return new
            {
                read = true,
                write = authorization.Satisfies(OAuthScopes.GlucoseReadWrite),
                success = true,
            };
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
    /// Request retro data load (replaces socket.io 'loadRetro' event). Each collection in the
    /// response is gated on its own read scope, so a narrowly-scoped credential receives only the
    /// categories it holds.
    /// </summary>
    /// <param name="request">Retro load request containing loadedMills timestamp</param>
    [HubScope(OAuthScopes.GlucoseRead)]
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
            var projectionService = serviceProvider?.GetService<DeviceStatusProjectionService>();

            if (entryService == null || treatmentService == null || projectionService == null)
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

            var authorization = HubAuthorizationState.Resolve(Context)!;

            // Load retro data from multiple collections. The glucose gate is the method's declared
            // scope; treatments and device status answer to their own.
            var entries = await entryService.GetEntriesAsync(
                find: $"{{\"mills\": {{\"$gte\": {startTime}, \"$lt\": {endTime}}}}}",
                count: 1000
            );

            IEnumerable<Core.Models.Treatment> treatments = [];
            if (authorization.Satisfies(OAuthScopes.TreatmentsRead))
            {
                treatments = await treatmentService.GetTreatmentsAsync(
                    find: $"{{\"mills\": {{\"$gte\": {startTime}, \"$lt\": {endTime}}}}}",
                    count: 1000
                );
            }

            IEnumerable<Core.Models.DeviceStatus> deviceStatuses = [];
            if (authorization.Satisfies(OAuthScopes.DevicesRead))
            {
                deviceStatuses = await projectionService.GetAsync(
                    count: 1000,
                    skip: 0,
                    find: null,
                    ct: default
                );
            }

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
    /// Subscribe to storage collections (replaces socket.io '/storage' namespace 'subscribe' event).
    /// Each collection is joined only when the connection's credential satisfies the read scope
    /// governing it, so the returned list may be narrower than the one requested.
    /// </summary>
    /// <param name="request">Storage subscription request</param>
    /// <returns>Subscription result</returns>
    public async Task<object> Subscribe(StorageSubscribeRequest request)
    {
        try
        {
            var governingScopes = RealtimeCategories.GoverningScopes;
            var collections = request.Collections ?? RealtimeCategories.All;
            var authorization = HubAuthorizationState.Resolve(Context)!;
            var subscribed = new List<string>();

            foreach (var collection in collections)
            {
                // An unclassified collection has no governing scope and cannot be subscribed to.
                if (!governingScopes.TryGetValue(collection, out var requiredScope))
                {
                    continue;
                }

                if (!authorization.Satisfies(requiredScope))
                {
                    _logger.LogDebug(
                        "Client {ConnectionId} lacks {RequiredScope} for collection {Collection}",
                        Context.ConnectionId,
                        requiredScope,
                        collection
                    );
                    continue;
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(collection));
                subscribed.Add(collection);
                _logger.LogDebug(
                    "Client {ConnectionId} subscribed to collection {Collection}",
                    Context.ConnectionId,
                    collection
                );
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
        // base.OnConnectedAsync() validates tenant context from the HTTP upgrade handshake
        await base.OnConnectedAsync();
        _logger.LogInformation(
            "Client {ConnectionId} connected to DataHub for tenant {TenantSlug}",
            Context.ConnectionId,
            TenantContext?.Slug
        );
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
