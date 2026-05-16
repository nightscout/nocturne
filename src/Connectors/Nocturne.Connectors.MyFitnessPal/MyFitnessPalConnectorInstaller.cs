using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.Connectors.MyFitnessPal;

public class MyFitnessPalConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "MyFitnessPal";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnectorConfiguration<MyFitnessPalConnectorConfiguration>(
            configuration,
            "MyFitnessPal"
        );
        if (!config.Enabled)
            return;

        // Server resolver — MyFitnessPal has a fixed URL, not a server mapping
        services.AddSingleton<IConnectorServerResolver<MyFitnessPalConnectorConfiguration>>(
            new ConnectorServerResolver<MyFitnessPalConnectorConfiguration>(null, null, null));
        services.AddSingleton<IConnectorConfigurationLoader<MyFitnessPalConnectorConfiguration>,
            ConnectorConfigurationLoader<MyFitnessPalConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        services
            .AddHttpClient<MyFitnessPalConnectorService>()
            .ConfigureConnectorClient("https://www.myfitnesspal.com");

        services.AddScoped<IConnectorSyncExecutor, MyFitnessPalSyncExecutor>();
    }
}

public class MyFitnessPalSyncExecutor
    : ConnectorSyncExecutor<MyFitnessPalConnectorService, MyFitnessPalConnectorConfiguration>
{
    public override string ConnectorId => "myfitnesspal";

    protected override string ConnectorName => "MyFitnessPal";
}
