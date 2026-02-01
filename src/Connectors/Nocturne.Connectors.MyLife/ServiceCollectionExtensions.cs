using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<MyLifeAuthTokenProvider>();

        services.AddSingleton<MyLifeDecryptor>();
        services.AddSingleton<MyLifeArchiveReader>();
        services.AddSingleton<MyLifeSyncService>();
        services.AddSingleton<MyLifeEventProcessor>();
        services.AddSingleton<MyLifeEventsCache>();
    }
}
