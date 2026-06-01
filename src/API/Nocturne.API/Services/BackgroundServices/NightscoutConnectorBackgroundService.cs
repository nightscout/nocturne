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

        foreach (var tenant in tenants)
        {
            try
            {
                using var tenantScope = ServiceProvider.CreateScope();

                var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                tenantAccessor.SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true));

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
                        tenant.Slug);
                    continue;
                }

                if (!config.Enabled || string.IsNullOrWhiteSpace(config.Url))
                    continue;

                var client = new SocketIO(new Uri(config.Url), new SocketIOOptions
                {
                    Reconnection = true,
                    ReconnectionAttempts = int.MaxValue,
                    ReconnectionDelayMax = 30_000,
                });

                var tenantId = tenant.Id;
                foreach (var evt in new[] { "dataUpdate", "create", "update" })
                    client.On(evt, _ => { RequestImmediateSync(tenantId); return Task.CompletedTask; });

                try
                {
                    await client.ConnectAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "Failed to connect Socket.IO for tenant {TenantSlug} at {Url}, will rely on polling",
                        tenant.Slug, config.Url);

                    client.Dispose();
                    continue;
                }

                _socketClients.TryAdd(tenantId, client);

                Logger.LogInformation(
                    "Started real-time listener for Nightscout tenant {TenantSlug}",
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
