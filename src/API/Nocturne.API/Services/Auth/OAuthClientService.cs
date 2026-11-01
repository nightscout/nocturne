using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth 2.0 client registrations and the known-application directory,
/// including Dynamic Client Registration (DCR) per RFC 7591.
/// </summary>
/// <seealso cref="IOAuthClientService"/>
/// <seealso cref="RedirectUriValidator"/>
public class OAuthClientService : IOAuthClientService
{
    private readonly IOAuthClientRepository _clients;
    private readonly RedirectUriValidator _redirectUriValidator;
    private readonly ILogger<OAuthClientService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthClientService"/>.
    /// </summary>
    /// <param name="redirectUriValidator">
    /// Validator that applies RFC 8252 redirect URI matching rules.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public OAuthClientService(
        IOAuthClientRepository clients,
        RedirectUriValidator redirectUriValidator,
        ILogger<OAuthClientService> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _redirectUriValidator = redirectUriValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthClientInfo?> GetClientAsync(string clientId, CancellationToken ct = default)
    {
        var client = await _clients.FindByClientIdAsync(clientId, ct);

        if (client == null)
        {
            _logger.LogDebug("OAuth client not found: {ClientId}", LogText.Sanitize(clientId));
            return null;
        }

        return MapToInfo(client);
    }

    /// <inheritdoc />
    public async Task<OAuthClientInfo?> GetClientByIdAsync(Guid id, CancellationToken ct = default)
    {
        var client = await _clients.GetAsync(id, ct);

        if (client == null)
        {
            _logger.LogDebug("OAuth client not found by ID {Id}", id);
            return null;
        }

        return MapToInfo(client);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateRedirectUriAsync(
        string clientId,
        string redirectUri,
        CancellationToken ct = default)
    {
        var client = await _clients.FindByClientIdAsync(clientId, ct);

        if (client == null)
        {
            _logger.LogWarning(
                "Redirect URI validation failed: client {ClientId} is not registered. " +
                "Apps must call POST /oauth/register before authorize.", LogText.Sanitize(clientId));
            return false;
        }

        var registered = client.RedirectUris;
        if (registered.Count == 0)
        {
            _logger.LogWarning(
                "Client {ClientId} has no registered redirect URIs", LogText.Sanitize(clientId));
            return false;
        }

        // RFC 8252 redirect URI matching: byte-exact except loopback allows any port
        return registered.Any(r => _redirectUriValidator.IsValidForAuthorize(r, redirectUri));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registration is anonymous, so the redirect URIs are the only thing tying a registrant to
    /// the app it names: the known-directory identity goes only to a registrant whose URIs the
    /// directory lists for its <paramref name="softwareId"/>. Anyone else keeps the
    /// <paramref name="softwareId"/> they sent but registers under their own name, unverified.
    /// </remarks>
    public async Task<OAuthClientInfo> RegisterClientAsync(
        string? softwareId,
        string? clientName,
        string? clientUri,
        string? logoUri,
        IReadOnlyList<string> redirectUris,
        string? scope,
        string? createdFromIp,
        CancellationToken ct = default)
    {
        var known = string.IsNullOrEmpty(softwareId)
            ? null
            : KnownOAuthClients.MatchBySoftwareId(softwareId);
        var verified = known != null && Covers(known.RedirectUris, redirectUris);

        var client = await _clients.CreateAsync(
            new NewOAuthClient(
                ClientId: Guid.CreateVersion7().ToString(),
                SoftwareId: softwareId,
                ClientName: verified ? known!.DisplayName : clientName,
                ClientUri: verified ? known!.Homepage : clientUri,
                LogoUri: verified ? known!.LogoUri : logoUri,
                DisplayName: verified ? known!.DisplayName : clientName,
                IsKnown: verified,
                RedirectUris: redirectUris,
                CreatedFromIp: createdFromIp),
            ct);

        _logger.LogInformation(
            "DCR: registered client {ClientId} software_id={SoftwareId} known={IsKnown}",
            client.ClientId, LogText.Sanitize(softwareId), client.IsKnown);

        return MapToInfo(client);
    }

    /// <summary>
    /// Whether every submitted redirect URI is one <paramref name="registered"/> accepts at
    /// authorize time. An empty submission is covered by anything: a client with no redirect URI
    /// can only use the device flow, whose approvals mark the grant
    /// <see cref="OAuthClientIdentity.Unverified"/>.
    /// </summary>
    private bool Covers(IReadOnlyCollection<string> registered, IReadOnlyList<string> submitted) =>
        submitted.All(s => registered.Any(r => _redirectUriValidator.IsValidForAuthorize(r, s)));

    private static OAuthClientInfo MapToInfo(OAuthClientRecord client)
    {
        return new OAuthClientInfo
        {
            Id = client.Id,
            ClientId = client.ClientId,
            DisplayName = client.DisplayName,
            ClientUri = client.ClientUri,
            LogoUri = client.LogoUri,
            SoftwareId = client.SoftwareId,
            IsKnown = client.IsKnown,
            RedirectUris = [.. client.RedirectUris],
        };
    }
}
