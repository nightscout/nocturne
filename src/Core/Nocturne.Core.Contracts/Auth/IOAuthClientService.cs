namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing OAuth client registrations and the known app directory.
/// </summary>
public interface IOAuthClientService
{
    /// <summary>
    /// Look up a client by its (tenant-scoped) client_id. Returns null if not
    /// found. Clients must be registered via DCR before calling this — there is
    /// no auto-create path.
    /// </summary>
    Task<OAuthClientInfo?> GetClientAsync(string clientId, CancellationToken ct = default);

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

    /// <summary>
    /// RFC 7591 Dynamic Client Registration. Every call registers a new client, so two installs of
    /// one app never share a <c>client_id</c> or a grant; an app keeps the <c>client_id</c> it was
    /// issued for as long as it refreshes its tokens.
    /// </summary>
    /// <param name="softwareId">RFC 7591 software_id (reverse-DNS), or null for anonymous clients.</param>
    /// <param name="clientName">Display name shown on the consent screen.</param>
    /// <param name="clientUri">Homepage URI for the application.</param>
    /// <param name="logoUri">Logo URI displayed on the consent screen.</param>
    /// <param name="redirectUris">Allowed redirect URIs (must already be validated).</param>
    /// <param name="scope">Space-delimited scope string.</param>
    /// <param name="createdFromIp">IP address that performed the registration request.</param>
    /// <returns>The registered <see cref="OAuthClientInfo"/>.</returns>
    Task<OAuthClientInfo> RegisterClientAsync(
        string? softwareId,
        string? clientName,
        string? clientUri,
        string? logoUri,
        IReadOnlyList<string> redirectUris,
        string? scope,
        string? createdFromIp,
        CancellationToken ct = default
    );
}

public class OAuthClientInfo
{
    public Guid Id { get; set; }

    /// <summary>The tenant-scoped OAuth client_id string.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Display name shown on the consent screen, if any.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Homepage URI for the application, if any.</summary>
    public string? ClientUri { get; set; }

    /// <summary>Logo URI displayed on the consent screen, if any.</summary>
    public string? LogoUri { get; set; }

    /// <summary>RFC 7591 software_id (reverse-DNS), or null for anonymous clients.</summary>
    public string? SoftwareId { get; set; }

    /// <summary>Whether this client comes from the bundled known-app directory.</summary>
    public bool IsKnown { get; set; }

    /// <summary>Allowed redirect URIs for this client.</summary>
    public List<string> RedirectUris { get; set; } = new();
}
