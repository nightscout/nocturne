using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
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
            glookoConfig.Server,
            new Dictionary<string, string>
            {
                ["US"] = GlookoConstants.Servers.Us,
                ["EU"] = GlookoConstants.Servers.Eu
            },
            GlookoConstants.Configuration.DefaultServer
        );

        var glookoHeaders = new Dictionary<string, string>
        {
            ["Accept-Encoding"] = "gzip, deflate, br"
        };
        const string glookoUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15";
        services.AddHttpClient<GlookoConnectorService>()
            .ConfigureConnectorClient(
                server,
                additionalHeaders: glookoHeaders,
                userAgent: glookoUserAgent,
                timeout: TimeSpan.FromMinutes(5),
                connectTimeout: TimeSpan.FromSeconds(15),
                addResilience: true);
        services.AddHttpClient<GlookoAuthTokenProvider>()
            .ConfigureConnectorClient(
                server,
                additionalHeaders: glookoHeaders,
                userAgent: glookoUserAgent,
                timeout: TimeSpan.FromMinutes(5),
                connectTimeout: TimeSpan.FromSeconds(15),
                addResilience: true);
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