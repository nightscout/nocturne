using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Services;

namespace Nocturne.Connectors.Eversense;

public class EversenseConnectorInstaller()
    : ConnectorInstaller<EversenseConnectorConfiguration, EversenseConnectorService, EversenseAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Eversense",
            ServerMapping = new Dictionary<string, string>
            {
                ["US"] = EversenseConstants.Servers.UsData
            },
            GetServerRegion = config => ((EversenseConnectorConfiguration)config).Server,
        });
