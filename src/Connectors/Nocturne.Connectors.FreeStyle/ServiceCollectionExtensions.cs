using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.Connectors.FreeStyle;

public static class ServiceCollectionExtensions
{
    public static void AddLibreLinkUpConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var libreConfig = services.AddConnectorConfiguration<LibreLinkUpConnectorConfiguration>(
            configuration,
            "LibreLinkUp"
        );
        if (!libreConfig.Enabled)
            return;

        var server = ConnectorServerResolver.Resolve(
            libreConfig.Region,
            new Dictionary<string, string>
            {
                ["AE"] = LibreLinkUpConstants.Endpoints.Ae,
                ["AP"] = LibreLinkUpConstants.Endpoints.Ap,
                ["AU"] = LibreLinkUpConstants.Endpoints.Au,
                ["CA"] = LibreLinkUpConstants.Endpoints.Ca,
                ["DE"] = LibreLinkUpConstants.Endpoints.De,
                ["EU"] = LibreLinkUpConstants.Endpoints.Eu,
                ["EU2"] = LibreLinkUpConstants.Endpoints.Eu2,
                ["FR"] = LibreLinkUpConstants.Endpoints.Fr,
                ["JP"] = LibreLinkUpConstants.Endpoints.Jp,
                ["US"] = LibreLinkUpConstants.Endpoints.Us
            },
            LibreLinkUpConstants.Endpoints.Eu
        );

        var libreHeaders = new Dictionary<string, string>
        {
            ["Version"] = "4.16.0",
            ["Product"] = "llu.android"
        };
        services.AddHttpClient<LibreConnectorService>()
            .ConfigureConnectorClient(server, additionalHeaders: libreHeaders);
        services.AddHttpClient<LibreLinkAuthTokenProvider>()
            .ConfigureConnectorClient(server, additionalHeaders: libreHeaders);

        // Register as Singleton to preserve token cache across requests
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(LibreLinkAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<LibreLinkUpConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<LibreLinkAuthTokenProvider>>();
            var retryStrategy = sp.GetRequiredService<IRetryDelayStrategy>();
            return new LibreLinkAuthTokenProvider(config, httpClient, logger, retryStrategy);
        });
    }
}