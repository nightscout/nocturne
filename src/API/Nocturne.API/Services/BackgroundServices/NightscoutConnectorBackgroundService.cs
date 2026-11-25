using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Web;
using SocketIOClient;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Polls a legacy Nightscout instance through <see cref="NightscoutConnectorService"/>, enabling
/// migration or mirroring workflows, and connects to each tenant's Nightscout Socket.IO endpoint to
/// sync at once when upstream data changes.
/// </summary>
public class NightscoutConnectorBackgroundService
    : ConnectorBackgroundService<NightscoutConnectorService, NightscoutConnectorConfiguration>,
        IConnectorRealtimeListeners
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ActiveTenantSnapshot _activeTenants;

    private readonly ConcurrentDictionary<Guid, SocketIO> _socketClients = new();

    /// <summary>
    /// Reconnection budget for a tenant's Socket.IO client. SocketIOClient bounds the whole
    /// connect-with-retries operation with <c>new CancellationTokenSource(ReconnectionAttempts *
    /// ReconnectionDelayMax)</c>, evaluated in <see cref="int"/> arithmetic: a product above
    /// <see cref="int.MaxValue"/> wraps negative and <c>ConnectAsync</c> throws
    /// <see cref="ArgumentOutOfRangeException"/> before it attempts a single connection. Keep
    /// <see cref="ReconnectionAttempts"/> * <see cref="ReconnectionDelayMaxMs"/> well inside int.
    /// </summary>
    internal const int ReconnectionAttempts = 3;

    /// <inheritdoc cref="ReconnectionAttempts"/>
    internal const int ReconnectionDelayMaxMs = 5_000;

    /// <summary>
    /// Per-tenant cap on establishing the initial Socket.IO connection. Tenants are connected
    /// concurrently and a failure falls back to polling, so this only bounds how long service
    /// startup waits on unreachable Nightscout instances.
    /// </summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many tenants' listeners connect at once.</summary>
    private const int ConcurrentConnects = 8;

    /// <param name="serviceProvider">Root provider each listener's configuration scope is opened from.</param>
    /// <param name="budget">The process-wide budget.</param>
    /// <param name="ledger">The job ledger.</param>
    /// <param name="activeTenants">The active tenants a listener is started for.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    /// <param name="timeProvider">Clock the schedule is computed against.</param>
    /// <param name="nudge">Delivers configuration writes for this connector.</param>
    /// <param name="metrics">Connector sync instruments.</param>
    /// <param name="runGuard">Refuses a sync for a key another run already holds.</param>
    public NightscoutConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ConnectorSyncBudget budget,
        IJobLedger ledger,
        ActiveTenantSnapshot activeTenants,
        ILogger<NightscoutConnectorBackgroundService> logger,
        TimeProvider timeProvider,
        ConnectorPollerNudge? nudge = null,
        ConnectorSyncMetrics? metrics = null,
        TenantRunGuard? runGuard = null
    )
        : base(budget, ledger, logger, timeProvider, nudge, metrics, runGuard)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _activeTenants = activeTenants ?? throw new ArgumentNullException(nameof(activeTenants));
    }

    /// <inheritdoc />
    public async Task StartRealtimeListenersAsync(CancellationToken cancellationToken)
    {
        var tenants = await _activeTenants.GetAsync(cancellationToken);

        // Connect tenants concurrently: each tenant waits up to ConnectTimeout, so connecting them in
        // sequence would hold the supervision pass for the sum of every unreachable instance's timeout.
        await Parallel.ForEachAsync(
            tenants,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ConcurrentConnects,
                CancellationToken = cancellationToken
            },
            async (tenant, ct) =>
            {
                try
                {
                    await StartListenerForTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(
                        ex,
                        "Unexpected error starting real-time listener for tenant {TenantSlug}",
                        tenant.Slug);
                }
            });
    }

    private async Task StartListenerForTenantAsync(
        Guid tenantId,
        string tenantSlug,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!await ListenerNeedsStartAsync(
                _socketClients, tenantId, tenantSlug, c => c.Connected, DisconnectAndDisposeAsync))
            return;

        await using var tenantScope = TenantJobScope.Open(
            _serviceProvider,
            new TenantContext(tenantId, tenantSlug, displayName, true, IsDemo: false),
            AuditEndpoint);

        var loader = tenantScope.ServiceProvider
            .GetRequiredService<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();

        NightscoutConnectorConfiguration config;
        try
        {
            config = await loader.LoadForTenantAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to load Nightscout config for tenant {TenantSlug}, skipping real-time listener",
                tenantSlug);
            return;
        }

        if (!config.Enabled || string.IsNullOrWhiteSpace(config.Url))
            return;

        // A deployment may expose bounded REST reads through an adapter while the original
        // Nightscout origin still provides Socket.IO. Keep Url as the polling source and use the
        // optional real-time origin only for the listener. Existing configurations fall back to
        // Url unchanged. Both values may be bare hosts, so normalise through the same helper the
        // sync path uses rather than parsing them directly.
        var realtimeUrl = string.IsNullOrWhiteSpace(config.RealtimeUrl)
            ? config.Url
            : config.RealtimeUrl;

        if (ResolveListenerBaseUrl(realtimeUrl, tenantSlug) is not { } resolvedUrl)
            return;

        var address = NightscoutAddress.Resolve(resolvedUrl);
        var client = NewClient(address, _serviceProvider.GetRequiredService<IHttpClientFactory>());

        foreach (var evt in new[] { "dataUpdate", "create", "update" })
            client.On(evt, _ => { RequestImmediateSync(tenantId); return Task.CompletedTask; });

        // Nightscout sends data events only to a socket that has authorized with read access, and
        // forgets it on every reconnect. It answers authorize with a full dataUpdate, so each
        // (re)connect syncs once: the catch-up for whatever changed while the socket was down.
        var authorize = AuthorizeMessage(config.ApiSecret, address.Token);
        client.OnConnected += (_, _) => Observe(AuthorizeAsync(client, authorize, tenantSlug), tenantSlug);

        try
        {
            using var deadline = new CancellationTokenSource(ConnectTimeout, Clock);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

            await client.ConnectAsync(connectCts.Token);
        }
        catch (Exception ex)
        {
            client.Dispose();

            // The service is shutting down — let the caller unwind rather than reporting a failure.
            if (cancellationToken.IsCancellationRequested)
                throw;

            Logger.LogWarning(
                ex,
                "Failed to connect Socket.IO for tenant {TenantSlug} at {Url}, will rely on polling",
                tenantSlug, address.Origin);

            return;
        }

        if (!_socketClients.TryAdd(tenantId, client))
        {
            await DisconnectAndDisposeAsync(client);
            return;
        }

        Logger.LogInformation(
            "Started real-time listener for Nightscout tenant {TenantSlug}",
            tenantSlug);
    }

    /// <summary>
    /// A client for the Socket.IO endpoint of <paramref name="address"/>. The client reads a URL's
    /// path as a Socket.IO namespace, so a site under a sub-path is reached by its origin and the
    /// endpoint path instead. The URL's query goes with the handshake, as it goes with every read.
    /// Its polling transport sends through the
    /// <see cref="NightscoutConnectorInstaller.RealtimeHttpClientName"/> connector client, which keeps
    /// the site's path out of telemetry, in place of the <see cref="HttpClient"/> the library would
    /// build itself. The client stays on polling, because a WebSocket would open its own connection
    /// outside that client's telemetry mark and its outbound guard.
    /// </summary>
    internal static SocketIO NewClient(NightscoutAddress address, IHttpClientFactory clients)
    {
        var options = new SocketIOOptions
        {
            Path = address.SocketIoPath,
            AutoUpgrade = false,
            Reconnection = true,
            ReconnectionAttempts = ReconnectionAttempts,
            ReconnectionDelayMax = ReconnectionDelayMaxMs,
        };
        if (address.Query is { } query)
            options.Query = HandshakeQuery(query);

        return new SocketIO(
            new Uri(address.Origin),
            options,
            services => services.AddSingleton(_ => clients.CreateClient(NightscoutConnectorInstaller.RealtimeHttpClientName)));
    }

    /// <summary>
    /// <paramref name="query"/> as SocketIOClient takes it. The client ignores a query on the URL it
    /// is given and form-encodes each value of <see cref="SocketIOOptions.Query"/> (a space as
    /// <c>+</c>, a <c>+</c> as <c>%2B</c>), so each value is form-decoded here and the handshake
    /// carries the bytes the reads send, up to the case of a percent escape. A key given twice is
    /// the one shape it cannot carry: the client joins its values with a comma.
    /// </summary>
    internal static NameValueCollection HandshakeQuery(string query) => HttpUtility.ParseQueryString(query);

    /// <summary>
    /// The <c>authorize</c> message Nightscout's own web client sends: the SHA-1 of the API secret
    /// and the access token, each only when there is one. With neither, Nightscout gives the
    /// socket its default role, which reads on a site that is readable without a secret.
    /// </summary>
    internal static Dictionary<string, object> AuthorizeMessage(string? apiSecret, string? token)
    {
        var message = new Dictionary<string, object> { ["client"] = "web", ["history"] = 1 };
        if (NightscoutConnectorService.ApiSecretHeaderValue(apiSecret) is { } secret)
            message["secret"] = secret;
        if (!string.IsNullOrEmpty(token))
            message["token"] = token;
        return message;
    }

    private async Task AuthorizeAsync(SocketIO client, Dictionary<string, object> message, string tenantSlug)
    {
        try
        {
            await client.EmitAsync("authorize", [message]);
        }
        catch (Exception ex) when (ex is SocketIOClient.Exceptions.ConnectionException
                                       or SocketIOClient.Session.ConnectionFailedException
                                       or InvalidOperationException or ObjectDisposedException or IOException
                                       or HttpRequestException or TimeoutException
                                       or OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Could not authorize the Socket.IO listener for tenant {TenantSlug}, will rely on polling",
                tenantSlug);
        }
    }

    /// <summary>
    /// Logs a fault of <paramref name="authorizing"/> that <see cref="AuthorizeAsync"/> did not
    /// expect. It runs from an event handler that nothing awaits, so the fault is observed here.
    /// </summary>
    private void Observe(Task authorizing, string tenantSlug) =>
        authorizing.ContinueWith(
            faulted => Logger.LogWarning(
                faulted.Exception,
                "Authorizing the Socket.IO listener for tenant {TenantSlug} failed, will rely on polling",
                tenantSlug),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async Task DisconnectAndDisposeAsync(SocketIO client)
    {
        await client.DisconnectAsync();
        client.Dispose();
    }

    /// <inheritdoc />
    public async Task StopRealtimeListenersAsync()
    {
        foreach (var (tenantId, client) in _socketClients)
        {
            try
            {
                await DisconnectAndDisposeAsync(client);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Error disconnecting Socket.IO client for tenant {TenantId}",
                    tenantId);
            }
        }

        _socketClients.Clear();

        Logger.LogInformation("Stopped all Nightscout real-time listeners");
    }
}
