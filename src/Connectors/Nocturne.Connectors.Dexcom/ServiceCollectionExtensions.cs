using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Configurations.Constants;
using Nocturne.Connectors.Dexcom.Services;

namespace Nocturne.Connectors.Dexcom;

public static class ServiceCollectionExtensions
{
    public static void AddDexcomConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var dexcomConfig = services.AddConnectorConfiguration<DexcomConnectorConfiguration>(
            configuration,
            "Dexcom"
        );
        if (!dexcomConfig.Enabled)
            return;

        var serverUrl = ConnectorServerResolver.Resolve(
            dexcomConfig.DexcomServer,
            new Dictionary<string, string>
            {
                ["US"] = DexcomConstants.Servers.Us,
                ["EU"] = DexcomConstants.Servers.Ous,
                ["OUS"] = DexcomConstants.Servers.Ous
            },
            dexcomConfig.DexcomServer
        );

        services.AddHttpClient<DexcomConnectorService>().ConfigureDexcomClient(serverUrl);
        services.AddHttpClient<DexcomAuthTokenProvider>().ConfigureDexcomClient(serverUrl);
    }
}
