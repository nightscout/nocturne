using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.Glooko.Xt;

namespace Nocturne.Connectors.Glooko;

public class GlookoConnectorInstaller()
    : ConnectorInstaller<GlookoConnectorConfiguration, GlookoConnectorService, GlookoAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Glooko",
            Timeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AddResilience = true,
        })
{
    /// <inheritdoc />
    protected override void InstallAdditional(IServiceCollection services, GlookoConnectorConfiguration config)
    {
        services.AddConnectorCredentialVerifier<GlookoCredentialVerifier>();

        // Glooko XT (Server = XT): the Socket.IO data client and the sign-in client the connect
        // controller drives. Same outbound guard as every connector client.
        services.TryAddSingleton<IGlookoXtDataClient, GlookoXtSocketDataClient>();
        services.AddHttpClient<GlookoXtLoginClient>()
            .ConfigureConnectorClient(
                null,
                timeout: TimeSpan.FromSeconds(30),
                connectTimeout: TimeSpan.FromSeconds(15),
                addResilience: true);
    }
}
