using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;

namespace Nocturne.Connectors.Twiist;

public class TwiistConnectorInstaller()
    : ConnectorInstaller<TwiistConnectorConfiguration, TwiistConnectorService, TwiistAuthTokenProvider>(
        new ConnectorOptions { ConnectorName = "Twiist" });
