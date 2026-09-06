using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;

namespace Nocturne.Connectors.Glooko;

public class GlookoConnectorInstaller()
    : ConnectorInstaller<GlookoConnectorConfiguration, GlookoConnectorService, GlookoAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Glooko",
            Timeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AddResilience = true,
        });
