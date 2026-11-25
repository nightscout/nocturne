using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Gluroo.Services;

public class GlurooConnectorService : NightscoutConnectorServiceBase<GlurooConnectorConfiguration>
{
    public GlurooConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<GlurooConnectorConfiguration> serverResolver,
        ILogger<GlurooConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorRegistration<GlurooConnectorConfiguration> registration,
        NightscoutLookups lookups,
        ITenantAccessor tenant,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, registration, lookups, tenant, publisher, timeProvider) { }

    public override string ServiceName => "Gluroo Global Connect";

}
