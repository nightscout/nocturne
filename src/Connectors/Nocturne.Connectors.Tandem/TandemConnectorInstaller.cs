using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Services;

namespace Nocturne.Connectors.Tandem;

public class TandemConnectorInstaller()
    : ConnectorInstaller<TandemConnectorConfiguration, TandemConnectorService, TandemAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "Tandem",
            // Long-running history fetches; allow generous timeouts and resilience policies.
            Timeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AddResilience = true,
        });
