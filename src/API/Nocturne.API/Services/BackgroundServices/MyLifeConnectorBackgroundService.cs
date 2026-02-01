using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for MyLife connector
/// </summary>
public class MyLifeConnectorBackgroundService : ConnectorBackgroundService<MyLifeConnectorConfiguration>
{
    public MyLifeConnectorBackgroundService(
        IServiceProvider serviceProvider,
        MyLifeConnectorConfiguration config,
        ILogger<MyLifeConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "MyLife";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<MyLifeConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
