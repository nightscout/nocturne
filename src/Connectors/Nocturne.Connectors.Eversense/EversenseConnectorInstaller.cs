using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Services;

namespace Nocturne.Connectors.Eversense;

public class EversenseConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Eversense";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<EversenseConnectorConfiguration, EversenseConnectorService, EversenseAuthTokenProvider>(
            configuration,
            new EversenseConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<EversenseAuthTokenProvider>();
        services.AddConnectorSyncExecutor<EversenseSyncExecutor>();
    }

    private sealed class EversenseConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public EversenseConnectorOptions()
        {
            ConnectorName = "Eversense";
            ServerMapping = new Dictionary<string, string>
            {
                ["US"] = EversenseConstants.Servers.UsData
            };
            GetServerRegion = config => ((EversenseConnectorConfiguration)config).Server;
        }
    }
}

public class EversenseSyncExecutor
    : ConnectorSyncExecutor<EversenseConnectorService, EversenseConnectorConfiguration>
{
    public override string ConnectorId => "eversense";

    protected override string ConnectorName => "Eversense";
}
