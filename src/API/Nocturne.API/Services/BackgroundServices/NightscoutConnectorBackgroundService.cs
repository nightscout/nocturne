using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using System.Collections.Concurrent;
using SocketIOClient;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs data from a legacy Nightscout instance via
/// <see cref="NightscoutConnectorService"/>, enabling migration or mirroring workflows.
/// Optionally connects to each tenant's Nightscout Socket.IO endpoint to trigger
/// immediate syncs when upstream data changes.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class NightscoutConnectorBackgroundService : ConnectorBackgroundService<NightscoutConnectorConfiguration>
{
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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public NightscoutConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<NightscoutConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "Nightscout";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, NightscoutConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<NightscoutConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }

    /// <inheritdoc />
    protected override async Task StartRealtimeListenersAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);

        var tenants = await context.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Slug, t.DisplayName })
            .ToListAsync(cancellationToken);

        // Connect tenants concurrently: each tenant waits up to ConnectTimeout, and the poll loop
        // does not start until this returns, so connecting them in sequence would delay the first
        // sync of every tenant by the sum of all unreachable instances' timeouts.
        await Parallel.ForEachAsync(
            tenants,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentTenantSyncs,
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
        using var tenantScope = ServiceProvider.CreateScope();

        var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        tenantAccessor.SetTenant(new TenantContext(tenantId, tenantSlug, displayName, true));

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

        // Tenants may store a bare host with no scheme. Normalise through the same helper the sync
        // path uses so a URL that polls fine does not fail here on Uri parsing.
        var socketUrl = NightscoutConnectorService.ResolveBaseUrl(config.Url);

        if (!Uri.TryCreate(socketUrl, UriKind.Absolute, out var socketUri))
        {
            Logger.LogWarning(
                "Nightscout URL {Url} for tenant {TenantSlug} is not a valid absolute URI, will rely on polling",
                socketUrl, tenantSlug);
            return;
        }

        var client = new SocketIO(socketUri, new SocketIOOptions
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

        _socketClients.TryAdd(tenantId, client);

        Logger.LogInformation(
            "Started real-time listener for Nightscout tenant {TenantSlug}",
            tenantSlug);
    }

    /// <inheritdoc />
    protected override async Task StopRealtimeListenersAsync()
    {
        foreach (var (tenantId, client) in _socketClients)
        {
            try
            {
                await client.DisconnectAsync();
                client.Dispose();
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
