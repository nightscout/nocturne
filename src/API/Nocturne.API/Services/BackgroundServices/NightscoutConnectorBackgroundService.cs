using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs data from a legacy Nightscout instance via
/// <see cref="NightscoutConnectorService"/>, enabling migration or mirroring workflows.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class NightscoutConnectorBackgroundService : ConnectorBackgroundService<NightscoutConnectorConfiguration>
{
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
}
