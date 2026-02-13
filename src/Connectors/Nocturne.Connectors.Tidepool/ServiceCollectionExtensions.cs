using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;

namespace Nocturne.Connectors.Tidepool;

public static class ServiceCollectionExtensions
{
    public static void AddTidepoolConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var tidepoolConfig = services.AddConnectorConfiguration<TidepoolConnectorConfiguration>(
            configuration,
            "Tidepool"
        );
        if (!tidepoolConfig.Enabled)
            return;

        var serverUrl = ConnectorServerResolver.Resolve(
            tidepoolConfig.Server,
            new Dictionary<string, string>
            {
                ["US"] = TidepoolConstants.Servers.Us,
                ["Development"] = TidepoolConstants.Servers.Development
            },
            tidepoolConfig.Server
        );

        services.AddHttpClient<TidepoolConnectorService>().ConfigureConnectorClient(serverUrl);
        services.AddHttpClient<TidepoolAuthTokenProvider>().ConfigureConnectorClient(serverUrl);

        // Register as Singleton to preserve token and userId cache across requests
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(TidepoolAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<TidepoolConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<TidepoolAuthTokenProvider>>();
            var retryStrategy = sp.GetRequiredService<IRetryDelayStrategy>();
            return new TidepoolAuthTokenProvider(config, httpClient, logger, retryStrategy);
        });
    }
}
