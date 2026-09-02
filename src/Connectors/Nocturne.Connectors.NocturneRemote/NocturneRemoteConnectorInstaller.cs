using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;

namespace Nocturne.Connectors.NocturneRemote;

public class NocturneRemoteConnectorInstaller()
    : TenantUrlConnectorInstaller<NocturneRemoteConnectorConfiguration, NocturneRemoteConnectorService>(
        new ConnectorOptions { ConnectorName = "NocturneRemote" },
        config => config.Url);
