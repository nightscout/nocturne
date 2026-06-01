using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using System.Collections.Concurrent;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically pulls data from a remote Nocturne V4 instance via
/// <see cref="NocturneRemoteConnectorService"/>.
/// Optionally connects to each tenant's Nocturne SignalR hub to trigger
/// immediate syncs when upstream data changes.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class NocturneRemoteConnectorBackgroundService : ConnectorBackgroundService<NocturneRemoteConnectorConfiguration>
{
    private readonly ConcurrentDictionary<Guid, HubConnection> _hubConnections = new();

    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public NocturneRemoteConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<NocturneRemoteConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "NocturneRemote";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, NocturneRemoteConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<NocturneRemoteConnectorService>();
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

                var hubUrl = $"{config.Url.TrimEnd('/')}/hubs/data";
                var tenantId = tenant.Id;

                var connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Headers.Add("Authorization", $"Bearer {config.Token}");
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

                _hubConnections.TryAdd(tenantId, connection);

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

    /// <inheritdoc />
    protected override async Task StopRealtimeListenersAsync()
    {
        foreach (var (tenantId, connection) in _hubConnections)
        {
            try
            {
                await connection.StopAsync();
                await connection.DisposeAsync();
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
