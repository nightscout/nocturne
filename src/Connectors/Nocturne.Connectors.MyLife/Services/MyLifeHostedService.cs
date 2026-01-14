using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeHostedService(
    IServiceProvider serviceProvider,
    ILogger<MyLifeHostedService> logger,
    IOptions<MyLifeConnectorConfiguration> config)
    : ResilientPollingHostedService<MyLifeConnectorService, MyLifeConnectorConfiguration>(
        serviceProvider,
        logger,
        config.Value)
{
    private readonly MyLifeConnectorConfiguration _config = config.Value;

    protected override string ConnectorName => "MyLife";

    protected override TimeSpan NormalPollingInterval =>
        TimeSpan.FromMinutes(_config.SyncIntervalMinutes);

    protected override async Task<bool> ExecuteSyncAsync(
        MyLifeConnectorService connector,
        DateTime? backfillFrom,
        CancellationToken cancellationToken)
    {
        return await connector.SyncDataAsync(_config, cancellationToken, backfillFrom);
    }
}