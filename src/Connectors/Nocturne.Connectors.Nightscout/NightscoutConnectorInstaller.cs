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

        // Direct singleton of the startup config. The connector service and write-back
        // sinks no longer take this dependency (they go through IConnectorRegistration /
        // IConnectorConfigurationLoader), but the compatibility proxy stack —
        // RequestForwardingService, NightscoutTransitionController, CompatibilityController,
        // CompatibilityProxyHealthCheck — still injects NightscoutConnectorConfiguration
        // directly. Those should be migrated to the loader pattern as a followup, at which
        // point this registration can be removed.
        services.AddSingleton(nightscoutConfig);

        if (!nightscoutConfig.Enabled)
            return;

        // Server resolver — Nightscout URLs come from per-tenant config, not a server mapping
        services.AddSingleton<IConnectorServerResolver<NightscoutConnectorConfiguration>>(
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null));
        services.AddScoped<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>,
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
