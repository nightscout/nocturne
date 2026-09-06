using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth 2.0 client registrations and the known-application directory,
/// including Dynamic Client Registration (DCR) per RFC 7591.
/// </summary>
/// <seealso cref="IOAuthClientService"/>
/// <seealso cref="RedirectUriValidator"/>
public class OAuthClientService : IOAuthClientService
{
    private readonly NocturneDbContext _dbContext;
    private readonly RedirectUriValidator _redirectUriValidator;
    private readonly ILogger<OAuthClientService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthClientService"/>.
    /// </summary>
    /// <param name="dbContext">The database context for reading and writing OAuth client records.</param>
    /// <param name="redirectUriValidator">
    /// Validator that applies RFC 8252 redirect URI matching rules.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public OAuthClientService(
        NocturneDbContext dbContext,
        RedirectUriValidator redirectUriValidator,
        ILogger<OAuthClientService> logger)
    {
        _dbContext = dbContext;
        _redirectUriValidator = redirectUriValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthClientInfo?> GetClientAsync(string clientId, CancellationToken ct = default)
    {
        var entity = await _dbContext.OAuthClients
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
        var entity = await _dbContext.OAuthClients
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
        var entity = await _dbContext.OAuthClients
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
    public async Task SeedKnownOAuthClientsAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Read existing software_ids for this tenant so we can skip already-seeded entries.
        var existingSoftwareIds = await _dbContext.OAuthClients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.SoftwareId != null)
            .Select(c => c.SoftwareId!)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existingSoftwareIds, StringComparer.Ordinal);

        var added = 0;
        foreach (var entry in KnownOAuthClients.Entries.Where(e => !existingSet.Contains(e.SoftwareId)))
        {
            _dbContext.OAuthClients.Add(new OAuthClientEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ClientId = Guid.CreateVersion7().ToString(),
                SoftwareId = entry.SoftwareId,
                ClientName = entry.DisplayName,
                ClientUri = entry.Homepage,
                LogoUri = entry.LogoUri,
                DisplayName = entry.DisplayName,
                IsKnown = true,
                RedirectUris = JsonSerializer.Serialize(entry.RedirectUris),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            added++;
        }

        if (added > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Seeded {Count} known OAuth clients for tenant {TenantId}",
                added, tenantId);
        }
    }

    /// <inheritdoc />
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
        // Idempotent on (tenant, software_id) when software_id is supplied — but only for
        // clients that completed a real registration. Known-directory seeds ship with an
        // empty RedirectUris list (the real value is only knowable once the client
        // registers), so returning such a seed unchanged would silently discard the
        // submitted redirect_uris and permanently break /authorize for that client.
        // A seed therefore adopts the incoming registration data instead.
        if (!string.IsNullOrEmpty(softwareId))
        {
            var existing = await _dbContext.OAuthClients
                .FirstOrDefaultAsync(c => c.SoftwareId == softwareId, ct);
            if (existing != null)
            {
                if (existing.IsKnown && DeserializeRedirectUris(existing.RedirectUris).Count == 0)
                {
                    existing.RedirectUris = JsonSerializer.Serialize(redirectUris);
                    existing.ClientName = clientName ?? existing.ClientName;
                    existing.ClientUri = clientUri ?? existing.ClientUri;
                    existing.LogoUri = logoUri ?? existing.LogoUri;
                    existing.DisplayName = clientName ?? existing.DisplayName;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(ct);

                    _logger.LogInformation(
                        "DCR: known-directory seed {SoftwareId} adopted registration redirect_uris ({Count} URIs)",
                        SanitizeForLog(softwareId), redirectUris.Count);
                    return MapToInfo(existing);
                }

                _logger.LogDebug(
                    "DCR: returning existing client for software_id {SoftwareId} (tenant {TenantId})",
                    SanitizeForLog(softwareId), existing.TenantId);
                return MapToInfo(existing);
            }
        }

        // Look up the known directory entry to mark is_known and pull display defaults.
        var known = string.IsNullOrEmpty(softwareId)
            ? null
            : KnownOAuthClients.MatchBySoftwareId(softwareId);

        var entity = new OAuthClientEntity
        {
            Id = Guid.CreateVersion7(),
            // client_id is opaque to clients; use the entity Id as a stable string.
            ClientId = Guid.CreateVersion7().ToString(),
            SoftwareId = softwareId,
            ClientName = clientName ?? known?.DisplayName,
            ClientUri = clientUri ?? known?.Homepage,
            LogoUri = logoUri ?? known?.LogoUri,
            DisplayName = clientName ?? known?.DisplayName,
            IsKnown = known != null,
            RedirectUris = JsonSerializer.Serialize(redirectUris),
            CreatedFromIp = createdFromIp,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.OAuthClients.Add(entity);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DCR: registered client {ClientId} software_id={SoftwareId} known={IsKnown}",
            entity.ClientId, SanitizeForLog(softwareId) ?? "(none)", entity.IsKnown);

        return MapToInfo(entity);
    }

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
