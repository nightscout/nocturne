using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs CGM and therapy data from the MyLife DiabetesApp via
/// <see cref="MyLifeConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class MyLifeConnectorBackgroundService : ConnectorBackgroundService<MyLifeConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public MyLifeConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<MyLifeConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "MyLife";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, MyLifeConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<MyLifeConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }
}
