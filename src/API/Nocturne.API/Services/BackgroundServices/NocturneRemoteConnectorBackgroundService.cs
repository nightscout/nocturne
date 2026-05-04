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
    /// <param name="config">NocturneRemote connector configuration (URL, token, polling interval, etc.).</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public NocturneRemoteConnectorBackgroundService(
        IServiceProvider serviceProvider,
        NocturneRemoteConnectorConfiguration config,
        ILogger<NocturneRemoteConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "NocturneRemote";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<NocturneRemoteConnectorService>();
        return await connectorService.SyncDataAsync(Config, cancellationToken, since: null, progressReporter);
    }
}
