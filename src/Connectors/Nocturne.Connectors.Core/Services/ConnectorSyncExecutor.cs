using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base class for connector sync executors. Handles service resolution,
///     DB config/secret loading, and delegation to the connector service.
/// </summary>
public abstract class ConnectorSyncExecutor<TService, TConfig> : IConnectorSyncExecutor
    where TService : class, IConnectorService<TConfig>
    where TConfig : class, IConnectorConfiguration
{
    public abstract string ConnectorId { get; }

    protected abstract string ConnectorName { get; }

    public async Task<SyncResult> ExecuteSyncAsync(
        IServiceProvider scopeProvider,
        SyncRequest request,
        CancellationToken ct,
        ISyncProgressReporter? progressReporter = null)
    {
        var service = scopeProvider.GetRequiredService<TService>();
        var config = scopeProvider.GetRequiredService<TConfig>();
        var logger = scopeProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(GetType());

        await LoadDatabaseConfigurationAsync(scopeProvider, config, logger, ct);

        return await service.SyncDataAsync(request, config, ct, progressReporter);
    }

    private async Task LoadDatabaseConfigurationAsync(
        IServiceProvider scopeProvider,
        TConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var configService = scopeProvider.GetRequiredService<IConnectorConfigurationService>();

            var dbConfig = await configService.GetConfigurationAsync(ConnectorName, ct);
            if (dbConfig?.Configuration != null)
                ConnectorConfigurationBinder.ApplyJsonToConfig(dbConfig.Configuration, config);

            var secrets = await configService.GetSecretsAsync(ConnectorName, ct);
            if (secrets.Count > 0)
                ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load database configuration for {ConnectorName} during manual sync",
                ConnectorName);
        }
    }
}
