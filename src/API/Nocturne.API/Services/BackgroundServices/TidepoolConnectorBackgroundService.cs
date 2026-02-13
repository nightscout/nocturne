using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for Tidepool connector
/// </summary>
public class TidepoolConnectorBackgroundService : ConnectorBackgroundService<TidepoolConnectorConfiguration>
{
    public TidepoolConnectorBackgroundService(
        IServiceProvider serviceProvider,
        TidepoolConnectorConfiguration config,
        ILogger<TidepoolConnectorBackgroundService> logger
    )
        : base(serviceProvider, config, logger) { }

    protected override string ConnectorName => "Tidepool";

    protected override async Task<bool> PerformSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var connectorService = scope.ServiceProvider.GetRequiredService<TidepoolConnectorService>();

        return await connectorService.SyncDataAsync(Config, cancellationToken);
    }
}
