using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs CGM data from the Dexcom Share cloud API via
/// <see cref="DexcomConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class DexcomConnectorBackgroundService : ConnectorBackgroundService<DexcomConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public DexcomConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<DexcomConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "Dexcom";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, DexcomConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<DexcomConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }
}
