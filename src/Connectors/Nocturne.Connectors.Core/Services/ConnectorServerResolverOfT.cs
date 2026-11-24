using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Extensions;
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
    private readonly Dictionary<string, string>? _serverMapping = serverMapping == null
        ? null
        : new Dictionary<string, string>(serverMapping, StringComparer.OrdinalIgnoreCase);

    public Uri? Resolve(TConfig config)
    {
        if (_serverMapping == null || getServerRegion == null)
            return defaultServer != null ? BuildUri(defaultServer) : null;

        var region = getServerRegion(config);
        var mapped = string.IsNullOrWhiteSpace(region)
            ? null
            : _serverMapping.GetValueOrDefault(region.Trim());
        var server = mapped ?? defaultServer ?? _serverMapping.Values.FirstOrDefault();
        return server != null ? BuildUri(server) : null;
    }

    public string BuildUrl(TConfig config, string path)
    {
        var baseUri = Resolve(config);
        return baseUri != null ? $"{baseUri.ToString().TrimEnd('/')}{path}" : path;
    }

    private static Uri BuildUri(string server)
        => new(ConnectorUrl.ResolveBase(server, ConnectorRegistrationAttribute.NameFor(typeof(TConfig))));
}
