using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;

[assembly: InternalsVisibleTo("Nocturne.Connectors.Dexcom.Tests")]

namespace Nocturne.Connectors.Dexcom;

public class DexcomConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Dexcom";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<DexcomConnectorConfiguration, DexcomConnectorService, DexcomAuthTokenProvider>(
            configuration,
            new DexcomConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<DexcomAuthTokenProvider>();
        services.AddConnectorSyncExecutor<ConnectorSyncExecutor<DexcomConnectorService, DexcomConnectorConfiguration>>();
    }

    private sealed class DexcomConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public DexcomConnectorOptions()
        {
            ConnectorName = "Dexcom";
            ServerMapping = new Dictionary<string, string>
            {
                ["US"] = DexcomConstants.Servers.Us,
                ["EU"] = DexcomConstants.Servers.Ous,
                ["OUS"] = DexcomConstants.Servers.Ous
            };
            GetServerRegion = config => ((DexcomConnectorConfiguration)config).Server;
        }
    }
}
