using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Nightscout.Services;

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

    public override string ServiceName => "Gluroo Global Connect";

}
