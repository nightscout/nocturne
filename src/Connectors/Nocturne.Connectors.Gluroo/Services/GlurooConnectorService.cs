using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Gluroo.Services;

public class GlurooConnectorService : NightscoutConnectorServiceBase<GlurooConnectorConfiguration>
{
    public GlurooConnectorService(
        HttpClient httpClient,
        ILogger<GlurooConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        GlurooConnectorConfiguration config,
        IConnectorPublisher? publisher = null
    ) : base(httpClient, logger, retryDelayStrategy, rateLimitingStrategy, config, publisher) { }

    protected override string ConnectorSource => DataSources.GlurooConnector;
    public override string ServiceName => "Gluroo Global Connect";

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.Notes,
        SyncDataType.Profiles
    ];
}
