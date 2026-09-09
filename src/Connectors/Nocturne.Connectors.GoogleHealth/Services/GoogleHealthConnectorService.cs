using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Health;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<GoogleHealthConnectorConfiguration> serverResolver,
    IGoogleHealthService googleHealth,
    ILogger<GoogleHealthConnectorService> logger,
    IConnectorPublisher? publisher = null)
    : BaseConnectorService<GoogleHealthConnectorConfiguration>(httpClient, serverResolver, logger, publisher)
{
    protected override string ConnectorSource => DataSources.GoogleHealthConnector;
    public override string ServiceName => ServiceNames.GoogleHealthConnector;
    protected override DateTime? InitialSyncFloor => null;

    public override Task<bool> AuthenticateAsync() => IsConnectedAsync(CancellationToken.None);

    protected override async Task<bool> EnsureAuthenticatedAsync(
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken) => await IsConnectedAsync(cancellationToken);

    private async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        var status = await googleHealth.StatusAsync(cancellationToken);
        return status.Configured && status.Connected;
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow };
        await googleHealth.SyncAsync(true, cancellationToken);
        var status = await googleHealth.StatusAsync(cancellationToken);

        result.Success = status.ErrorCode is null or "partial_consent";
        result.EndTime = DateTimeOffset.UtcNow;
        if (!result.Success)
        {
            result.Message = status.ErrorCode!;
            result.Errors.Add(status.ErrorCode!);
        }
        return result;
    }
}
