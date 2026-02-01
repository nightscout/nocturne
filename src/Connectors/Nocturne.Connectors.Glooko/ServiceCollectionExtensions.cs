using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Configurations.Constants;
using Nocturne.Connectors.Glooko.Services;

namespace Nocturne.Connectors.Glooko;

public static class ServiceCollectionExtensions
{
    public static void AddGlookoConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var glookoConfig = services.AddConnectorConfiguration<GlookoConnectorConfiguration>(
            configuration,
            "Glooko"
        );
        if (!glookoConfig.Enabled)
            return;

        var server = ConnectorServerResolver.Resolve(
            glookoConfig.GlookoServer,
            new Dictionary<string, string>
            {
                ["US"] = GlookoConstants.Servers.Us,
                ["EU"] = GlookoConstants.Servers.Eu
            },
            GlookoConstants.Configuration.DefaultServer
        );

        services.AddHttpClient<GlookoConnectorService>().ConfigureGlookoClient(server);
        services.AddHttpClient<GlookoAuthTokenProvider>().ConfigureGlookoClient(server);
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(GlookoAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<GlookoConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<GlookoAuthTokenProvider>>();
            return new GlookoAuthTokenProvider(config, httpClient, logger);
        });

    }
}
