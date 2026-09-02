using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;

namespace Nocturne.Connectors.MyLife;

public class MyLifeConnectorInstaller : IConnectorInstaller
{
    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnectorConfiguration<MyLifeConnectorConfiguration>(
            configuration,
            "MyLife"
        );
        if (!config.Enabled)
            return;

        // Register server resolver, config loader, and token cache
        services.AddSingleton<IConnectorServerResolver<MyLifeConnectorConfiguration>>(
            new ConnectorServerResolver<MyLifeConnectorConfiguration>(null, null, null));
        services.AddScoped<IConnectorConfigurationLoader<MyLifeConnectorConfiguration>,
            ConnectorConfigurationLoader<MyLifeConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        // ConfigureConnectorClient, not a bare AddHttpClient: it installs LinkLocalGuardHandler and
        // turns off transport-level redirects. ServiceUrl is member-supplied (declared
        // Format = "uri" on MyLifeConnectorConfiguration) and reaches the SOAP and REST calls
        // through the session, so a bare registration let a member aim these clients at the cloud
        // metadata endpoint and read the outcome off connector status.
        services.AddHttpClient<MyLifeSoapClient>().ConfigureConnectorClient(null);
        services.AddHttpClient<MyLifeAuthTokenProvider>().ConfigureConnectorClient(null);
        services.AddHttpClient<MyLifeConnectorService>().ConfigureConnectorClient(null);
        services.AddSingleton<IMyLifeSessionCache, MyLifeSessionCache>();
        services.AddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IMyLifeSessionCache>());

        services.AddConnectorTokenProvider<MyLifeAuthTokenProvider>();

        services.AddSingleton<MyLifeSyncService>();
        services.AddSingleton<MyLifeEventProcessor>();

        services.AddConnectorSyncExecutor<ConnectorSyncExecutor<MyLifeConnectorService, MyLifeConnectorConfiguration>>();
    }
}
