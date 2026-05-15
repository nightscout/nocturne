using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs diabetes device data from Tidepool via
/// <see cref="TidepoolConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class TidepoolConnectorBackgroundService : ConnectorBackgroundService<TidepoolConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public TidepoolConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<TidepoolConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "Tidepool";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, TidepoolConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<TidepoolConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }
}
