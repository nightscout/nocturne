using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Services.WriteBack;

namespace Nocturne.Connectors.Nightscout;

public class NightscoutConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Nightscout";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var nightscoutConfig = services.AddConnectorConfiguration<NightscoutConnectorConfiguration>(
            configuration,
            "Nightscout");

        if (!nightscoutConfig.Enabled)
            return;

        // Server resolver — Nightscout URLs come from per-tenant config, not a server mapping
        services.AddSingleton<IConnectorServerResolver<NightscoutConnectorConfiguration>>(
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null));
        services.AddSingleton<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>,
            ConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        // URL comes from user config (possibly loaded from DB at runtime),
        // so configure it at registration time only if already available.
        if (!string.IsNullOrEmpty(nightscoutConfig.Url))
            services.AddHttpClient<NightscoutConnectorService>()
                .ConfigureConnectorClient(nightscoutConfig.Url);
        else
            services.AddHttpClient<NightscoutConnectorService>();

        services.AddScoped<IConnectorSyncExecutor, NightscoutSyncExecutor>();

        // Write-back sinks (circuit breaker is shared singleton, sinks are scoped)
        services.AddSingleton<NightscoutCircuitBreaker>();

        void RegisterWriteBackClient<TSink>() where TSink : class
        {
            if (!string.IsNullOrEmpty(nightscoutConfig.Url))
                services.AddHttpClient<TSink>()
                    .ConfigureConnectorClient(nightscoutConfig.Url);
            else
                services.AddHttpClient<TSink>();
        }

        RegisterWriteBackClient<NightscoutEntryWriteBackSink>();
        RegisterWriteBackClient<NightscoutTreatmentWriteBackSink>();
        RegisterWriteBackClient<NightscoutDeviceStatusWriteBackSink>();
        RegisterWriteBackClient<NightscoutProfileWriteBackSink>();
        RegisterWriteBackClient<NightscoutFoodWriteBackSink>();
        RegisterWriteBackClient<NightscoutActivityWriteBackSink>();
    }
}

public class NightscoutSyncExecutor
    : ConnectorSyncExecutor<NightscoutConnectorService, NightscoutConnectorConfiguration>
{
    public override string ConnectorId => "nightscout";

    protected override string ConnectorName => "Nightscout";
}
