using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

public class ConnectorRegistration<TConfig>(TConfig defaults, string connectorName)
    : IConnectorRegistration<TConfig>
{
    public TConfig Defaults { get; } = defaults;
    public string ConnectorName { get; } = connectorName;
}
