using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs CGM and device data from the Medtronic CareLink
/// cloud API via <see cref="CareLinkConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class CareLinkConnectorBackgroundService : ConnectorBackgroundService<CareLinkConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="config">CareLink connector configuration (credentials, polling interval, etc.).</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public CareLinkConnectorBackgroundService(
        IServiceProvider serviceProvider,
        CareLinkConnectorConfiguration config,
        ILogger<CareLinkConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "CareLink";

    protected override async Task<SyncResult> PerformSyncAsync(
        IServiceProvider scopeProvider,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<CareLinkConnectorService>();
        return await connectorService.SyncDataAsync(Config, cancellationToken, since: null, progressReporter);
    }
}
