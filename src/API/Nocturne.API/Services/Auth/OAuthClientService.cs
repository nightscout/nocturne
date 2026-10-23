using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth 2.0 client registrations and the known-application directory,
/// including Dynamic Client Registration (DCR) per RFC 7591.
/// </summary>
/// <seealso cref="IOAuthClientService"/>
/// <seealso cref="RedirectUriValidator"/>
public class OAuthClientService : IOAuthClientService
{
    private readonly INocturneDbContextFactory _contexts;
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
        INocturneDbContextFactory contexts,
        RedirectUriValidator redirectUriValidator,
        ILogger<OAuthClientService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _redirectUriValidator = redirectUriValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthClientInfo?> GetClientAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.OAuthClients
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

        if (entity == null)
        {
            _logger.LogDebug("OAuth client not found: {ClientId}", SanitizeForLog(clientId));
            return null;
        }

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<OAuthClientInfo?> GetClientByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.OAuthClients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity == null)
        {
            _logger.LogDebug("OAuth client not found by ID {Id}", id);
            return null;
        }

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateRedirectUriAsync(
        string clientId,
        string redirectUri,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.OAuthClients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

        if (entity == null)
        {
            _logger.LogWarning(
                "Redirect URI validation failed: client {ClientId} is not registered. " +
                "Apps must call POST /oauth/register before authorize.", SanitizeForLog(clientId));
            return false;
        }

        var registered = DeserializeRedirectUris(entity.RedirectUris);
        if (registered.Count == 0)
        {
            _logger.LogWarning(
                "Client {ClientId} has no registered redirect URIs", SanitizeForLog(clientId));
            return false;
        }

        // RFC 8252 redirect URI matching: byte-exact except loopback allows any port
        return registered.Any(r => _redirectUriValidator.IsValidForAuthorize(r, redirectUri));
    }

    /// <summary>
    /// Deserialises the <c>redirect_uris</c> JSON array stored in a client entity.
    /// Returns an empty list on parse failure or when the JSON is blank.
    /// </summary>
    /// <param name="redirectUrisJson">Raw JSON string from <see cref="Nocturne.Infrastructure.Data.Entities.OAuthClientEntity.RedirectUris"/>.</param>
    /// <returns>A list of registered redirect URI strings.</returns>
    private static List<string> DeserializeRedirectUris(string redirectUrisJson)
    {
        if (string.IsNullOrWhiteSpace(redirectUrisJson) || redirectUrisJson == "[]")
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(redirectUrisJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
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

        var entity = new OAuthClientEntity
        {
            Id = Guid.CreateVersion7(),
            ClientId = Guid.CreateVersion7().ToString(),
            SoftwareId = softwareId,
            ClientName = verified ? known!.DisplayName : clientName,
            ClientUri = verified ? known!.Homepage : clientUri,
            LogoUri = verified ? known!.LogoUri : logoUri,
            DisplayName = verified ? known!.DisplayName : clientName,
            IsKnown = verified,
            RedirectUris = JsonSerializer.Serialize(redirectUris),
            CreatedFromIp = createdFromIp,
        };

        await using var db = await _contexts.CreateAsync(ct);
        db.OAuthClients.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DCR: registered client {ClientId} software_id={SoftwareId} known={IsKnown}",
            entity.ClientId, SanitizeForLog(softwareId) ?? "(none)", entity.IsKnown);

        return MapToInfo(entity);
    }

    /// <summary>
    /// Whether every submitted redirect URI is one <paramref name="registered"/> accepts at
    /// authorize time. An empty submission is covered by anything: a client with no redirect URI
    /// can only use the device flow, whose approvals mark the grant
    /// <see cref="OAuthClientIdentity.Unverified"/>.
    /// </summary>
    private bool Covers(IReadOnlyCollection<string> registered, IReadOnlyList<string> submitted) =>
        submitted.All(s => registered.Any(r => _redirectUriValidator.IsValidForAuthorize(r, s)));

    /// <summary>
    /// Strips control characters (including CR/LF) from a user-supplied value before it reaches a
    /// log sink. Structured-logging placeholders already prevent message injection, but CodeQL
    /// cannot prove that — and single-line values are friendlier to downstream log tooling.
    /// Values are also truncated to keep log lines bounded.
    /// </summary>
    /// <param name="value">The raw user-supplied string to sanitise, or <see langword="null"/>.</param>
    /// <param name="maxLength">Maximum number of characters to retain (default: 200).</param>
    /// <returns>A sanitised string with control characters replaced by <c>_</c>, or <see langword="null"/>.</returns>
    private static string? SanitizeForLog(string? value, int maxLength = 200)
    {
        if (value is null)
            return null;

        var buffer = new char[Math.Min(value.Length, maxLength)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = value[i];
            buffer[i] = char.IsControl(c) ? '_' : c;
        }
        return new string(buffer);
    }

    /// <summary>
    /// Maps an <see cref="OAuthClientEntity"/> to an <see cref="OAuthClientInfo"/> DTO.
    /// </summary>
    /// <param name="entity">The database entity to map.</param>
    /// <returns>A populated <see cref="OAuthClientInfo"/> view model.</returns>
    private static OAuthClientInfo MapToInfo(OAuthClientEntity entity)
    {
        return new OAuthClientInfo
        {
            Id = entity.Id,
            ClientId = entity.ClientId,
            DisplayName = entity.DisplayName,
            ClientUri = entity.ClientUri,
            LogoUri = entity.LogoUri,
            SoftwareId = entity.SoftwareId,
            IsKnown = entity.IsKnown,
            RedirectUris = DeserializeRedirectUris(entity.RedirectUris)
        };
    }
}
