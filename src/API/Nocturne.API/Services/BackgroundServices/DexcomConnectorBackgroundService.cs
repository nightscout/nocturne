using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for Dexcom Share connector
/// </summary>
public class DexcomConnectorBackgroundService : ConnectorBackgroundService<DexcomConnectorConfiguration>
{
    public DexcomConnectorBackgroundService(
        IServiceProvider serviceProvider,
        DexcomConnectorConfiguration config,
        ILogger<DexcomConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "Dexcom";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<DexcomConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
