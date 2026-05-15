using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that periodically syncs data from Gluroo Global Connect via
/// <see cref="GlurooConnectorService"/>.
/// </summary>
/// <seealso cref="ConnectorBackgroundService{TConfig}"/>
public class GlurooConnectorBackgroundService : ConnectorBackgroundService<GlurooConnectorConfiguration>
{
    public GlurooConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<GlurooConnectorBackgroundService> logger
    )
        : base(serviceProvider, logger) { }

    protected override string ConnectorName => "Gluroo";

    protected override async Task<SyncResult> PerformSyncAsync(IServiceProvider scopeProvider, GlurooConnectorConfiguration config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null)
    {
        var connectorService = scopeProvider.GetRequiredService<GlurooConnectorService>();
        return await connectorService.SyncDataAsync(config, cancellationToken, since: null, progressReporter);
    }
}
