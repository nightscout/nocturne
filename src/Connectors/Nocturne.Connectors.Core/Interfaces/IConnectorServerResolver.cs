using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Resolves the base server URL for a connector from its current configuration.
///     Singleton service. Holds the static server mapping; reads the region from the per-tenant config at call time.
/// </summary>
public interface IConnectorServerResolver<in TConfig>
    where TConfig : BaseConnectorConfiguration
{
    /// <summary>
    ///     Returns the absolute base URI for the given config's server/region setting,
    ///     or null if no server mapping is configured.
    /// </summary>
    Uri? Resolve(TConfig config);

    /// <summary>
    ///     Convenience: resolves the base URI and appends the given path.
    ///     If no server mapping is configured, returns the path unchanged.
    /// </summary>
    string BuildUrl(TConfig config, string path);
}
