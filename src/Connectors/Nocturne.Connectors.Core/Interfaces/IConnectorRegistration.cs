namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Frozen, read-only carrier for IConfiguration-bound startup defaults.
///     NOT the runtime per-tenant config — use IConnectorConfigurationLoader for that.
/// </summary>
public interface IConnectorRegistration<out TConfig>
{
    TConfig Defaults { get; }
    string ConnectorName { get; }
}
