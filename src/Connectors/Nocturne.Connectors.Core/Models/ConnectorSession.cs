namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Cached authentication session for a connector+tenant pair.
///     Metadata carries connector-specific extras (session cookie, refresh token, etc.).
/// </summary>
public record ConnectorSession(
    string Token,
    DateTime ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata = null);
