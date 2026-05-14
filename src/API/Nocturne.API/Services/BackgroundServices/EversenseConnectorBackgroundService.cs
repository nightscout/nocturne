using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs CGM data from the Eversense Now cloud API via
/// <see cref="EversenseConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class EversenseConnectorBackgroundService : ConnectorBackgroundService<EversenseConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="config">Eversense connector configuration (credentials, polling interval, etc.).</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public EversenseConnectorBackgroundService(
        IServiceProvider serviceProvider,
        EversenseConnectorConfiguration config,
        ILogger<EversenseConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "Eversense";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<EversenseConnectorService>();
        return await connectorService.SyncDataAsync(Config, cancellationToken, since: null, progressReporter);
    }
}
