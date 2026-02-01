using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for Glooko connector
/// </summary>
public class GlookoConnectorBackgroundService
    : ConnectorBackgroundService<GlookoConnectorConfiguration>
{
    public GlookoConnectorBackgroundService(
        IServiceProvider serviceProvider,
        GlookoConnectorConfiguration config,
        ILogger<GlookoConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "Glooko";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<GlookoConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
