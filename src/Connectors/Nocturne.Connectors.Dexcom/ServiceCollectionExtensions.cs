using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Dexcom.Configurations;
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
            dexcomConfig.Server,
            new Dictionary<string, string>
            {
                ["US"] = DexcomConstants.Servers.Us,
                ["EU"] = DexcomConstants.Servers.Ous,
                ["OUS"] = DexcomConstants.Servers.Ous
            },
            dexcomConfig.Server
        );

        services.AddHttpClient<DexcomConnectorService>().ConfigureConnectorClient(serverUrl);
        services.AddHttpClient<DexcomAuthTokenProvider>().ConfigureConnectorClient(serverUrl);

        // Register as Singleton to preserve token cache across requests
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(DexcomAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<DexcomConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<DexcomAuthTokenProvider>>();
            var retryStrategy = sp.GetRequiredService<IRetryDelayStrategy>();
            return new DexcomAuthTokenProvider(config, httpClient, logger, retryStrategy);
        });
    }
}