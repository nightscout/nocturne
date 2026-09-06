using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;

namespace Nocturne.Connectors.Tidepool;

public class TidepoolConnectorInstaller()
    : ConnectorInstaller<TidepoolConnectorConfiguration, TidepoolConnectorService, TidepoolAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Tidepool",
            ServerMapping = new Dictionary<string, string>
            {
                ["US"] = TidepoolConstants.Servers.Us,
                ["Development"] = TidepoolConstants.Servers.Development
            },
            GetServerRegion = config => ((TidepoolConnectorConfiguration)config).Server,
        });
