namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing OAuth client registrations and the known app directory.
/// </summary>
public interface IOAuthClientService
{
    /// <summary>
    /// Find or create a client record for the given client_id.
    /// If the client_id matches a known app, metadata is populated from the directory.
    /// </summary>
    Task<OAuthClientInfo> FindOrCreateClientAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Get client info by internal ID.
    /// </summary>
    Task<OAuthClientInfo?> GetClientByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Check if a redirect URI is valid for the given client.
    /// For unknown clients, any HTTPS URI is accepted on first use.
    /// </summary>
    Task<bool> ValidateRedirectUriAsync(
        string clientId,
        string redirectUri,
        CancellationToken ct = default
    );
}

/// <summary>
/// OAuth client information returned by the client service.
/// </summary>
public class OAuthClientInfo
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsKnown { get; set; }
    public List<string> RedirectUris { get; set; } = new();
}
