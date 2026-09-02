using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Services.WriteBack;

namespace Nocturne.Connectors.Nightscout;

public class NightscoutConnectorInstaller()
    : TenantUrlConnectorInstaller<NightscoutConnectorConfiguration, NightscoutConnectorService>(
        new ConnectorOptions { ConnectorName = "Nightscout" },
        config => config.Url)
{
    /// <summary>
    ///     A direct singleton of the startup config. The connector service and write-back sinks go
    ///     through <see cref="IConnectorRegistration{TConfig}"/> /
    ///     <see cref="IConnectorConfigurationLoader{TConfig}"/>, but the compatibility proxy stack —
    ///     RequestForwardingService, NightscoutTransitionController, CompatibilityController,
    ///     CompatibilityProxyHealthCheck — still injects
    ///     <see cref="NightscoutConnectorConfiguration"/> directly. Migrating those to the loader
    ///     pattern is what would let this registration go.
    /// </summary>
    protected override void InstallUnconditional(
        IServiceCollection services,
        NightscoutConnectorConfiguration config) =>
        services.AddSingleton(config);

    protected override void InstallAdditional(
        IServiceCollection services,
        NightscoutConnectorConfiguration config)
    {
        services.AddSingleton<NightscoutCircuitBreaker>();

        void RegisterWriteBackClient<TSink>() where TSink : class =>
            ConfigureClient(services.AddHttpClient<TSink>(), config);

        RegisterWriteBackClient<NightscoutEntryWriteBackSink>();
        RegisterWriteBackClient<NightscoutTreatmentWriteBackSink>();
        RegisterWriteBackClient<NightscoutDeviceStatusWriteBackSink>();
        RegisterWriteBackClient<NightscoutProfileWriteBackSink>();
        RegisterWriteBackClient<NightscoutFoodWriteBackSink>();
        RegisterWriteBackClient<NightscoutActivityWriteBackSink>();
    }
}
