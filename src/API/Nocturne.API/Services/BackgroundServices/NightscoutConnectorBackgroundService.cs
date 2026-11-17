using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using System.Collections.Concurrent;
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

        if (ResolveListenerBaseUrl(realtimeUrl, tenantSlug) is not { } socketUrl)
            return;

        var client = new SocketIO(new Uri(socketUrl), new SocketIOOptions
        {
            Reconnection = true,
            ReconnectionAttempts = ReconnectionAttempts,
            ReconnectionDelayMax = ReconnectionDelayMaxMs,
        });

        foreach (var evt in new[] { "dataUpdate", "create", "update" })
            client.On(evt, _ => { RequestImmediateSync(tenantId); return Task.CompletedTask; });

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);

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
                tenantSlug, socketUrl);

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
