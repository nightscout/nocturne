using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;

namespace Nocturne.Connectors.Dexcom;

public class DexcomConnectorInstaller()
    : ConnectorInstaller<DexcomConnectorConfiguration, DexcomConnectorService, DexcomAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Dexcom",
            ServerMapping = new Dictionary<string, string>
            {
                ["US"] = DexcomConstants.Servers.Us,
                ["EU"] = DexcomConstants.Servers.Ous,
                ["OUS"] = DexcomConstants.Servers.Ous
            },
            GetServerRegion = config => ((DexcomConnectorConfiguration)config).Server,
        });
