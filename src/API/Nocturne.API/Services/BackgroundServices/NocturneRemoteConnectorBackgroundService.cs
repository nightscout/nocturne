using Nocturne.API.Multitenancy;
using Microsoft.AspNetCore.SignalR.Client;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using System.Collections.Concurrent;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Pulls data from a remote Nocturne V4 instance through <see cref="NocturneRemoteConnectorService"/>,
/// and connects to each tenant's Nocturne SignalR hub to sync at once when upstream data changes.
/// </summary>
public class NocturneRemoteConnectorBackgroundService
    : ConnectorBackgroundService<NocturneRemoteConnectorService, NocturneRemoteConnectorConfiguration>,
        IConnectorRealtimeListeners
{
    private readonly ConcurrentDictionary<Guid, HubConnection> _hubConnections = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ActiveTenantSnapshot _activeTenants;

    /// <param name="serviceProvider">Root provider each listener's configuration scope is opened from.</param>
    /// <param name="budget">The process-wide budget.</param>
    /// <param name="ledger">The job ledger.</param>
    /// <param name="activeTenants">The active tenants a listener is started for.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    /// <param name="timeProvider">Clock the schedule is computed against.</param>
    /// <param name="nudge">Delivers configuration writes for this connector.</param>
    /// <param name="metrics">Connector sync instruments.</param>
    /// <param name="runGuard">Refuses a sync for a key another run already holds.</param>
    public NocturneRemoteConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ConnectorSyncBudget budget,
        IJobLedger ledger,
        ActiveTenantSnapshot activeTenants,
        ILogger<NocturneRemoteConnectorBackgroundService> logger,
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

        foreach (var tenant in tenants)
        {
            try
            {
                // Reconnection is infinite, so anything short of Disconnected is still a live listener.
                if (!await ListenerNeedsStartAsync(
                        _hubConnections, tenant.Id, tenant.Slug,
                        c => c.State != HubConnectionState.Disconnected, StopAndDisposeAsync))
                    continue;

                await using var tenantScope = TenantJobScope.Open(
                    _serviceProvider,
                    new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                    AuditEndpoint);

                var loader = tenantScope.ServiceProvider
                    .GetRequiredService<IConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>>();

                NocturneRemoteConnectorConfiguration config;
                try
                {
                    config = await loader.LoadForTenantAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "Failed to load NocturneRemote config for tenant {TenantSlug}, skipping real-time listener",
                        tenant.Slug);
                    continue;
                }

                if (!config.Enabled || string.IsNullOrWhiteSpace(config.Url))
                    continue;

                if (ResolveListenerBaseUrl(config.Url, tenant.Slug) is not { } baseUrl)
                    continue;

                var hubUrl = $"{baseUrl}/hubs/data";
                var tenantId = tenant.Id;

                var connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Headers.Add("Authorization", $"Bearer {config.AccessToken}");
                    })
                    .WithAutomaticReconnect(new InfiniteRetryPolicy())
                    .Build();

                foreach (var evt in new[] { "dataUpdate", "create", "update" })
                    connection.On<object>(evt, _ => RequestImmediateSync(tenantId));

                try
                {
                    await connection.StartAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "Failed to connect SignalR for tenant {TenantSlug} at {Url}, will rely on polling",
                        tenant.Slug, hubUrl);

                    await connection.DisposeAsync();
                    continue;
                }

                if (!_hubConnections.TryAdd(tenantId, connection))
                {
                    await StopAndDisposeAsync(connection);
                    continue;
                }

                Logger.LogInformation(
                    "Started real-time listener for NocturneRemote tenant {TenantSlug}",
                    tenant.Slug);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Unexpected error starting real-time listener for tenant {TenantSlug}",
                    tenant.Slug);
            }
        }
    }

    private static async Task StopAndDisposeAsync(HubConnection connection)
    {
        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    /// <inheritdoc />
    public async Task StopRealtimeListenersAsync()
    {
        foreach (var (tenantId, connection) in _hubConnections)
        {
            try
            {
                await StopAndDisposeAsync(connection);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Error disconnecting SignalR client for tenant {TenantId}",
                    tenantId);
            }
        }

        _hubConnections.Clear();

        Logger.LogInformation("Stopped all NocturneRemote real-time listeners");
    }

    private sealed class InfiniteRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var delay = Math.Min(1000 * Math.Pow(2, retryContext.PreviousRetryCount), 30_000);
            return TimeSpan.FromMilliseconds(delay);
        }
    }
}
