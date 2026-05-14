using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.CareLink;

public class CareLinkConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "CareLink";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<CareLinkConnectorConfiguration, CareLinkConnectorService, CareLinkAuthTokenProvider>(
            configuration,
            new CareLinkConnectorOptions());

        if (config == null) return;

        services.AddConnectorTokenProvider<CareLinkAuthTokenProvider>();
        services.AddConnectorSyncExecutor<CareLinkSyncExecutor>();
    }

    private sealed class CareLinkConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public CareLinkConnectorOptions()
        {
            ConnectorName = "CareLink";
            ServerMapping = new Dictionary<string, string>
            {
                ["EU"] = $"https://{CareLinkConstants.Servers.Eu}",
                ["US"] = $"https://{CareLinkConstants.Servers.Us}",
            };
            GetServerRegion = config => ((CareLinkConnectorConfiguration)config).Server;
        }
    }
}

public class CareLinkSyncExecutor
    : ConnectorSyncExecutor<CareLinkConnectorService, CareLinkConnectorConfiguration>
{
    public override string ConnectorId => "carelink";
    protected override string ConnectorName => "CareLink";
}
