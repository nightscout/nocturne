using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.CareLink;

public class CareLinkConnectorInstaller()
    : ConnectorInstaller<CareLinkConnectorConfiguration, CareLinkConnectorService, CareLinkAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "CareLink",
            ServerMapping = new Dictionary<string, string>
            {
                ["EU"] = $"https://{CareLinkConstants.Servers.Eu}",
                ["US"] = $"https://{CareLinkConstants.Servers.Us}",
            },
            GetServerRegion = config => ((CareLinkConnectorConfiguration)config).Server,
        });
