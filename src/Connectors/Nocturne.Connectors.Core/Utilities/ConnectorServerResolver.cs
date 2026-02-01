namespace Nocturne.Connectors.Core.Utilities;

public static class ConnectorServerResolver
{
    public static string Resolve(
        string? region,
        IReadOnlyDictionary<string, string> map,
        string defaultServer)
    {
        if (string.IsNullOrWhiteSpace(region))
            return defaultServer;

        var key = region.Trim().ToUpperInvariant();
        if (map.TryGetValue(key, out var server))
            return server;

        return defaultServer;
    }
}
