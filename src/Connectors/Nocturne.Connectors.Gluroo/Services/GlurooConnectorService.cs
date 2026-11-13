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
        IConnectorRegistration<GlurooConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, registration, publisher, timeProvider) { }

    protected override string ConnectorSource => DataSources.GlurooConnector;
    public override string ServiceName => "Gluroo Global Connect";

}
