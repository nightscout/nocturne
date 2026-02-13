using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;

namespace Nocturne.Connectors.MyLife;

public static class ServiceCollectionExtensions
{
    public static void AddMyLifeConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var config = services.AddConnectorConfiguration<MyLifeConnectorConfiguration>(
            configuration,
            "MyLife"
        );
        if (!config.Enabled)
            return;

        services.AddHttpClient<MyLifeSoapClient>();
        services.AddHttpClient<MyLifeAuthTokenProvider>();
        services.AddHttpClient<MyLifeConnectorService>();
        services.AddSingleton<MyLifeSessionStore>();

        // Register as Singleton to preserve token cache across requests
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(MyLifeAuthTokenProvider));
            var options = sp.GetRequiredService<IOptions<MyLifeConnectorConfiguration>>();
            var soapClient = sp.GetRequiredService<MyLifeSoapClient>();
            var sessionStore = sp.GetRequiredService<MyLifeSessionStore>();
            var logger = sp.GetRequiredService<ILogger<MyLifeAuthTokenProvider>>();
            return new MyLifeAuthTokenProvider(options, httpClient, soapClient, sessionStore, logger);
        });

        services.AddSingleton<MyLifeSyncService>();
        services.AddSingleton<MyLifeEventProcessor>();
        services.AddSingleton<MyLifeEventsCache>();
    }
}