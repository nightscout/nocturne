using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base class for connector sync executors. Handles service resolution
///     and per-tenant config loading via <see cref="IConnectorConfigurationLoader{TConfig}"/>,
///     then delegates to the connector service.
/// </summary>
public abstract class ConnectorSyncExecutor<TService, TConfig> : IConnectorSyncExecutor
    where TService : class, IConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    public abstract string ConnectorId { get; }

    protected abstract string ConnectorName { get; }

    public async Task<SyncResult> ExecuteSyncAsync(
        IServiceProvider scopeProvider,
        SyncRequest request,
        CancellationToken ct,
        ISyncProgressReporter? progressReporter = null)
    {
        var logger = scopeProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        var loader = scopeProvider.GetRequiredService<IConnectorConfigurationLoader<TConfig>>();

        var config = await loader.LoadForTenantAsync(scopeProvider, ct);

        var service = scopeProvider.GetRequiredService<TService>();
        return await service.SyncDataAsync(request, config, ct, progressReporter);
    }
}
