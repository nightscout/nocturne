using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for MyFitnessPal connector
/// </summary>
public class MyFitnessPalConnectorBackgroundService : ConnectorBackgroundService<MyFitnessPalConnectorConfiguration>
{
    public MyFitnessPalConnectorBackgroundService(
        IServiceProvider serviceProvider,
        MyFitnessPalConnectorConfiguration config,
        ILogger<MyFitnessPalConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "MyFitnessPal";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<MyFitnessPalConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
