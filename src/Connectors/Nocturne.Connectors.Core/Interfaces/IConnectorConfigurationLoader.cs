namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Loads per-tenant connector configuration by composing startup defaults
///     with DB-stored config and secrets. Returns a fresh instance per call.
/// </summary>
public interface IConnectorConfigurationLoader<TConfig>
{
    Task<TConfig> LoadForTenantAsync(CancellationToken ct);
}
