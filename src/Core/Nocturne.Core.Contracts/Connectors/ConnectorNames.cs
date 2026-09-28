namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
///     The one rule for turning any spelling of a connector name into the form it is stored,
///     dispatched and routed under.
/// </summary>
/// <remarks>
///     Connector names reach the storage layer in two spellings: the PascalCase name a connector's
///     own C# constants and its registration attribute declare, and the lowercase id the tenant UI
///     puts in a route. Both must resolve to the same configuration row, so the name is folded here
///     on the way in rather than compared case-insensitively on the way out — a case-insensitive
///     comparison has to be remembered at every call site, and one that forgets writes a row the
///     others cannot see.
/// </remarks>
public static class ConnectorNames
{
    /// <summary>
    ///     The canonical form of <paramref name="connectorName"/>.
    /// </summary>
    public static string Canonical(string connectorName) =>
        connectorName?.ToLowerInvariant() ?? string.Empty;
}
