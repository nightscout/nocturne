using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically pulls data from a remote Nocturne V4 instance via
/// <see cref="NocturneRemoteConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class NocturneRemoteConnectorBackgroundService : ConnectorBackgroundService<NocturneRemoteConnectorConfiguration>
{
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
}
