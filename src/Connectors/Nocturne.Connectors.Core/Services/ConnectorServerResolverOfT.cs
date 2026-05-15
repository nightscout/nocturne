using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Resolves the base server URL for a connector using the static server mapping
///     held from startup, combined with the per-tenant config's region at call time.
/// </summary>
public class ConnectorServerResolver<TConfig>(
    IReadOnlyDictionary<string, string>? serverMapping,
    Func<BaseConnectorConfiguration, string>? getServerRegion,
    string? defaultServer)
    : IConnectorServerResolver<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    public Uri? Resolve(TConfig config)
    {
        if (serverMapping == null || getServerRegion == null)
            return defaultServer != null ? BuildUri(defaultServer) : null;

        var region = getServerRegion(config);
        var fallback = defaultServer ?? region ?? serverMapping.Values.FirstOrDefault() ?? "";
        var resolved = ConnectorServerResolver.Resolve(region, serverMapping, fallback);
        return BuildUri(resolved);
    }

    public string BuildUrl(TConfig config, string path)
    {
        var baseUri = Resolve(config);
        return baseUri != null ? $"{baseUri.ToString().TrimEnd('/')}{path}" : path;
    }

    private static Uri BuildUri(string server)
    {
        var url = server.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? server
            : $"https://{server}";
        return new Uri(url);
    }
}
