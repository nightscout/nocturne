using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for FreeStyle LibreLinkUp connector
/// </summary>
public class FreeStyleConnectorBackgroundService
    : ConnectorBackgroundService<LibreLinkUpConnectorConfiguration>
{
    public FreeStyleConnectorBackgroundService(
        IServiceProvider serviceProvider,
        LibreLinkUpConnectorConfiguration config,
        ILogger<FreeStyleConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "FreeStyle LibreLinkUp";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<LibreConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
