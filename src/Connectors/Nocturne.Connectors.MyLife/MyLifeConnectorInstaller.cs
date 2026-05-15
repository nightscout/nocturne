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
    public string ConnectorName => "MyLife";

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
        services.AddSingleton<IConnectorConfigurationLoader<MyLifeConnectorConfiguration>,
            ConnectorConfigurationLoader<MyLifeConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        services.AddHttpClient<MyLifeSoapClient>();
        services.AddHttpClient<MyLifeAuthTokenProvider>();
        services.AddHttpClient<MyLifeConnectorService>();
        services.AddSingleton<IMyLifeSessionCache, MyLifeSessionCache>();
        services.AddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IMyLifeSessionCache>());

        services.AddConnectorTokenProvider<MyLifeAuthTokenProvider>();

        services.AddSingleton<MyLifeSyncService>();
        services.AddSingleton<MyLifeEventProcessor>();

        services.AddScoped<IConnectorSyncExecutor, MyLifeSyncExecutor>();
    }
}

public class MyLifeSyncExecutor
    : ConnectorSyncExecutor<MyLifeConnectorService, MyLifeConnectorConfiguration>
{
    public override string ConnectorId => "mylife";

    protected override string ConnectorName => "MyLife";
}
