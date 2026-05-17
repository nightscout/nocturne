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
        IConnectorServerResolver<GlurooConnectorConfiguration> serverResolver,
        ILogger<GlurooConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<GlurooConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, rateLimitingStrategy, registration, publisher) { }

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
