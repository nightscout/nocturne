using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs CGM data from Abbott FreeStyle LibreLinkUp via
/// <see cref="LibreConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class FreeStyleConnectorBackgroundService
    : ConnectorBackgroundService<LibreLinkUpConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    public FreeStyleConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<FreeStyleConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "FreeStyle LibreLinkUp";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<LibreConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }
}
