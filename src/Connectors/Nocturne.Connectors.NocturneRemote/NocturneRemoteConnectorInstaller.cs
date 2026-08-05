using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;

namespace Nocturne.Connectors.NocturneRemote;

public class NocturneRemoteConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "NocturneRemote";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnectorConfiguration<NocturneRemoteConnectorConfiguration>(
            configuration,
            "NocturneRemote");

        if (!config.Enabled)
            return;

        // Server resolver — URLs come from per-tenant config, not a server mapping
        services.AddSingleton<IConnectorServerResolver<NocturneRemoteConnectorConfiguration>>(
            new ConnectorServerResolver<NocturneRemoteConnectorConfiguration>(null, null, null));
        services.AddScoped<IConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>,
            ConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        // URL comes from user config (possibly loaded from DB at runtime),
        // so configure it at registration time only if already available.
        // ConfigureConnectorClient unconditionally — the URL only decides whether there is a
        // BaseAddress to set. The comment above is the whole point: the URL normally arrives from
        // per-tenant configuration at runtime, so at startup it is empty and the bare branch is the
        // one that actually runs in production. That branch skipped LinkLocalGuardHandler and left
        // transport redirects on, which meant the guard was absent for exactly the connectors whose
        // base URL a member supplies.
        services.AddHttpClient<NocturneRemoteConnectorService>()
            .ConfigureConnectorClient(string.IsNullOrEmpty(config.Url) ? null : config.Url);

        services.AddScoped<IConnectorSyncExecutor, NocturneRemoteSyncExecutor>();
    }
}

public class NocturneRemoteSyncExecutor
    : ConnectorSyncExecutor<NocturneRemoteConnectorService, NocturneRemoteConnectorConfiguration>
{
    public override string ConnectorId => "nocturneremote";

    protected override string ConnectorName => "NocturneRemote";
}
