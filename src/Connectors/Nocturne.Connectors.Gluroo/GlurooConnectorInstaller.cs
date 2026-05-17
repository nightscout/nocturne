using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;

namespace Nocturne.Connectors.Gluroo;

public class GlurooConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Gluroo";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var glurooConfig = services.AddConnectorConfiguration<GlurooConnectorConfiguration>(
            configuration,
            "Gluroo");

        if (!glurooConfig.Enabled)
            return;

        // Server resolver — Gluroo URLs come from per-tenant config, not a server mapping
        services.AddSingleton<IConnectorServerResolver<GlurooConnectorConfiguration>>(
            new ConnectorServerResolver<GlurooConnectorConfiguration>(null, null, null));
        services.AddScoped<IConnectorConfigurationLoader<GlurooConnectorConfiguration>,
            ConnectorConfigurationLoader<GlurooConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        if (!string.IsNullOrEmpty(glurooConfig.Url))
            services.AddHttpClient<GlurooConnectorService>()
                .ConfigureConnectorClient(glurooConfig.Url);
        else
            services.AddHttpClient<GlurooConnectorService>();

        services.AddScoped<IConnectorSyncExecutor, GlurooSyncExecutor>();
    }
}

public class GlurooSyncExecutor
    : ConnectorSyncExecutor<GlurooConnectorService, GlurooConnectorConfiguration>
{
    public override string ConnectorId => "gluroo";

    protected override string ConnectorName => "Gluroo";
}
