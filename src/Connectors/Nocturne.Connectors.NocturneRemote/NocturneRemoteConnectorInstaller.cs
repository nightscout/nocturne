using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // URL comes from user config (possibly loaded from DB at runtime),
        // so configure it at registration time only if already available.
        if (!string.IsNullOrEmpty(config.Url))
            services.AddHttpClient<NocturneRemoteConnectorService>()
                .ConfigureConnectorClient(config.Url);
        else
            services.AddHttpClient<NocturneRemoteConnectorService>();

        services.AddScoped<IConnectorSyncExecutor, NocturneRemoteSyncExecutor>();
    }
}

public class NocturneRemoteSyncExecutor
    : ConnectorSyncExecutor<NocturneRemoteConnectorService, NocturneRemoteConnectorConfiguration>
{
    public override string ConnectorId => "nocturneremote";

    protected override string ConnectorName => "NocturneRemote";
}
