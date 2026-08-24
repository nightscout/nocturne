using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Orchestrates OAuth token operations: authorization code exchange,
/// refresh token rotation, and revocation.
/// </summary>
/// <seealso cref="IOAuthTokenService"/>
/// <seealso cref="IJwtService"/>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IOAuthGrantService"/>
/// <seealso cref="RefreshTokenService"/>
public class OAuthTokenService : IOAuthTokenService
{
    private readonly NocturneDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly ISubjectService _subjectService;
    private readonly IOAuthGrantService _grantService;
    private readonly IOAuthTokenRevocationCache _revocationCache;
    private readonly ILogger<OAuthTokenService> _logger;

    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Initializes a new instance of <see cref="OAuthTokenService"/>.
    /// </summary>
    /// <param name="db">The EF Core database context for authorization code and token entity access.</param>
    /// <param name="jwtService">Service for generating and validating JWT access tokens and refresh tokens.</param>
    /// <param name="subjectService">Service for resolving subject permissions and roles for token claims.</param>
    /// <param name="grantService">Service for persisting and querying OAuth consent grants.</param>
    /// <param name="revocationCache">Blocklist of revoked access tokens, keyed by <c>jti</c>.</param>
    /// <param name="logger">The logger instance.</param>
    public OAuthTokenService(
        NocturneDbContext db,
        IJwtService jwtService,
        ISubjectService subjectService,
        IOAuthGrantService grantService,
        IOAuthTokenRevocationCache revocationCache,
        ILogger<OAuthTokenService> logger
    )
    {
        _db = db;
        _jwtService = jwtService;
        _subjectService = subjectService;
        _grantService = grantService;
        _revocationCache = revocationCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAuthorizationCodeAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string redirectUri,
        string codeChallenge,
        bool limitTo24Hours = false,
        CancellationToken ct = default
    )
    {
        var code = _jwtService.GenerateRefreshToken(); // Reuse the crypto-random generator
        var codeHash = _jwtService.HashRefreshToken(code);

        var entity = new Infrastructure.Data.Entities.OAuthAuthorizationCodeEntity
        {
            ClientEntityId = clientEntityId,
            SubjectId = subjectId,
            CodeHash = codeHash,
            Scopes = scopes.ToList(),
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            ExpiresAt = DateTime.UtcNow.Add(AuthorizationCodeLifetime),
            LimitTo24Hours = limitTo24Hours,
        };

        _db.OAuthAuthorizationCodes.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Generated authorization code for client entity {ClientEntityId}, subject {SubjectId}, limitTo24Hours={LimitTo24Hours}",
            clientEntityId,
            subjectId,
            limitTo24Hours
        );

        return code;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        CancellationToken ct = default
    )
    {
        var codeHash = _jwtService.HashRefreshToken(code);
        var now = DateTime.UtcNow;

        // Claim the code before anything else. Redemption state is not a concurrency token and the
        // unique index is on the hash, so a read-then-write check let two simultaneous exchanges
        // both see redeemed_at IS NULL and both mint a token pair. This single statement is the
        // atomic claim: exactly one caller can move redeemed_at away from NULL.
        var claimed = await _db.OAuthAuthorizationCodes
            .Where(c => c.CodeHash == codeHash && c.RedeemedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedeemedAt, now), ct);

        if (claimed == 0)
        {
            // Unknown, expired and already-redeemed are one error: RFC 6749 Section 5.2 maps all
            // three to invalid_grant, and a single message tells a caller nothing about which code
            // hashes exist. On a genuine replay the tokens already issued from the code are revoked
            // (RFC 6749 Section 4.1.2, OAuth 2.0 Security BCP Section 4.1.2).
            await RevokeGrantOnCodeReplayAsync(codeHash, ct);
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code is invalid.");
        }

        var authCode = await _db.OAuthAuthorizationCodes
            .AsNoTracking()
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

        if (authCode == null)
        {
            // The row was claimed a moment ago, so this only happens if it was deleted meanwhile.
            _logger.LogWarning("Authorization code exchange failed: claimed code no longer readable");
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code is invalid.");
        }

        // Verify client_id matches
        if (authCode.Client?.ClientId != clientId)
        {
            _logger.LogWarning("Authorization code exchange failed: client_id mismatch");
            return OAuthTokenResult.Fail("invalid_grant", "Client ID does not match.");
        }

        // Verify redirect_uri matches
        if (authCode.RedirectUri != redirectUri)
        {
            _logger.LogWarning("Authorization code exchange failed: redirect_uri mismatch");
            return OAuthTokenResult.Fail("invalid_grant", "Redirect URI does not match.");
        }

        // Validate PKCE
        if (!PkceValidator.ValidateCodeChallenge(codeVerifier, authCode.CodeChallenge))
        {
            _logger.LogWarning("Authorization code exchange failed: PKCE validation failed");
            return OAuthTokenResult.Fail("invalid_grant", "PKCE code_verifier validation failed.");
        }

        // Create or update grant
        var grant = await _grantService.CreateOrUpdateGrantAsync(
            authCode.ClientEntityId,
            authCode.SubjectId,
            authCode.Scopes,
            ct: ct
        );

        // Mint tokens
        return await MintTokenPairAsync(grant, ct);
    }

    /// <summary>
    /// Handles a failed claim on an authorization code. When the code exists and was already
    /// redeemed, the exchange is a replay: whoever holds the code is racing the legitimate client or
    /// replaying a stolen one, and the tokens issued from the first redemption can no longer be
    /// trusted, so the grant behind them is revoked. An unknown or merely expired code is nothing to
    /// act on.
    /// </summary>
    private async Task RevokeGrantOnCodeReplayAsync(string codeHash, CancellationToken ct)
    {
        var replayed = await _db.OAuthAuthorizationCodes
            .AsNoTracking()
            .Where(c => c.CodeHash == codeHash && c.RedeemedAt != null)
            .Select(c => new { c.ClientEntityId, c.SubjectId })
            .FirstOrDefaultAsync(ct);

        if (replayed == null)
        {
            _logger.LogWarning("Authorization code exchange failed: code unknown or expired");
            return;
        }

        _logger.LogWarning(
            "Authorization code replay detected for subject {SubjectId}", replayed.SubjectId);

        var grant = await _grantService.GetActiveGrantAsync(
            replayed.ClientEntityId, replayed.SubjectId, ct);

        if (grant != null)
        {
            await _grantService.RevokeGrantAsync(grant.Id, ct);
        }
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        string? clientId,
        CancellationToken ct = default
    )
    {
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var oauthToken = await _db.OAuthRefreshTokens
            .Include(t => t.Grant)
                .ThenInclude(g => g!.Client)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (oauthToken == null)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Refresh token is invalid.");
        }

        // Token reuse detection: if the token was already revoked but has a replacement,
        // this is a potential token theft — revoke the entire grant
        if (oauthToken.IsRevoked)
        {
            if (oauthToken.ReplacedById != null && oauthToken.Grant != null)
            {
                _logger.LogWarning(
                    "Refresh token reuse detected for grant {GrantId}. Revoking entire grant.",
                    oauthToken.GrantId
                );
                await _grantService.RevokeGrantAsync(oauthToken.GrantId, ct);
            }

            return OAuthTokenResult.Fail("invalid_grant", "Refresh token has been revoked.");
        }

        // Check expiry
        if (oauthToken.IsExpired)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Refresh token has expired.");
        }

        // Verify grant is still active
        if (oauthToken.Grant == null || oauthToken.Grant.IsRevoked)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Authorization grant has been revoked.");
        }

        // Verify client_id if provided
        if (!string.IsNullOrEmpty(clientId) && oauthToken.Grant.Client?.ClientId != clientId)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Client ID does not match.");
        }

        // Rotate: revoke old token, create new one
        oauthToken.RevokedAt = DateTime.UtcNow;

        var newRefreshToken = _jwtService.GenerateRefreshToken();
        var newTokenHash = _jwtService.HashRefreshToken(newRefreshToken);

        var newTokenEntity = new Infrastructure.Data.Entities.OAuthRefreshTokenEntity
        {
            TenantId = oauthToken.Grant.TenantId,
            GrantId = oauthToken.GrantId,
            TokenHash = newTokenHash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        };

        _db.OAuthRefreshTokens.Add(newTokenEntity);

        // Set the rotation chain link (UUID v7 assigns Id at Add() time, so it's available before save)
        oauthToken.ReplacedById = newTokenEntity.Id;
        await _db.SaveChangesAsync(ct);

        // Update grant last used
        await _grantService.UpdateLastUsedAsync(oauthToken.GrantId, null, null, ct);

        // Mint access token
        var grant = oauthToken.Grant;
        var subject = await _subjectService.GetSubjectByIdAsync(grant.SubjectId);
        if (subject == null)
        {
            return OAuthTokenResult.Fail("server_error", "Subject not found.");
        }

        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
        };

        var roles = await _subjectService.GetSubjectRolesAsync(subject.Id);
        var permissions = ScopeTranslator.ToPermissions(grant.Scopes).ToList();

        var accessToken = _jwtService.GenerateAccessToken(
            subjectInfo,
            permissions,
            roles,
            grant.Scopes,
            grant.Client?.ClientId,
            tenantId: grant.TenantId,
            grantId: grant.Id
        );

        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;
        var scopeStr = string.Join(" ", grant.Scopes);

        return OAuthTokenResult.Ok(accessToken, newRefreshToken, expiresIn, scopeStr);
    }

    /// <inheritdoc />
    public async Task RevokeTokenAsync(
        string token,
        string? tokenTypeHint,
        CancellationToken ct = default
    )
    {
        // Per RFC 7009 Section 2.1 the hint is advisory: if it doesn't resolve the token, the
        // server extends the search to the other types. Both types are therefore always tried.
        if (await RevokeAsRefreshTokenAsync(token, ct))
        {
            return;
        }

        if (await RevokeAsAccessTokenAsync(token, ct))
        {
            return;
        }

        // Per RFC 7009: always return success even if token not found
        _logger.LogDebug("Token revocation: token not found (this is normal per RFC 7009)");
    }

    /// <summary>
    /// Revokes <paramref name="token"/> if it is a stored refresh token. Returns whether it was.
    /// </summary>
    private async Task<bool> RevokeAsRefreshTokenAsync(string token, CancellationToken ct)
    {
        var tokenHash = _jwtService.HashRefreshToken(token);

        var refreshToken = await _db.OAuthRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.RevokedAt == null, ct);

        if (refreshToken == null)
        {
            return false;
        }

        refreshToken.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "OAuthAudit: {Event} token_id={TokenId} grant_id={GrantId}",
            "refresh_token_revoked", refreshToken.Id, refreshToken.GrantId);
        return true;
    }

    /// <summary>
    /// Blocklists <paramref name="token"/> by its <c>jti</c> if it is a valid access-token JWT.
    /// Returns whether it was. The blocklist entry lives only as long as the token would have,
    /// which is all a stateless token needs.
    /// </summary>
    private async Task<bool> RevokeAsAccessTokenAsync(string token, CancellationToken ct)
    {
        if (!TokenFormat.IsJwt(token))
        {
            return false;
        }

        var validation = _jwtService.ValidateAccessToken(token);
        if (!validation.IsValid || validation.Claims is null)
        {
            return false;
        }

        var claims = validation.Claims;
        if (string.IsNullOrEmpty(claims.JwtId))
        {
            return false;
        }

        var remainingLifetime = claims.ExpiresAt - DateTimeOffset.UtcNow;
        await _revocationCache.RevokeAsync(claims.JwtId, remainingLifetime, ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} jti={Jti} grant_id={GrantId}",
            "access_token_revoked", claims.JwtId, claims.GrantId);
        return true;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> ExchangeDeviceCodeAsync(
        string deviceCode,
        string clientId,
        CancellationToken ct = default
    )
    {
        var codeHash = _jwtService.HashRefreshToken(deviceCode);

        var entity = await _db.OAuthDeviceCodes
            .FirstOrDefaultAsync(d => d.DeviceCodeHash == codeHash, ct);

        if (entity == null)
        {
            _logger.LogWarning("Device code exchange failed: code not found");
            return OAuthTokenResult.Fail("invalid_grant", "Device code is invalid.");
        }

        if (entity.ClientId != clientId)
        {
            _logger.LogWarning("Device code exchange failed: client_id mismatch");
            return OAuthTokenResult.Fail("invalid_grant", "Client ID does not match.");
        }

        if (entity.IsExpired)
        {
            _logger.LogWarning("Device code exchange failed: code expired");
            return OAuthTokenResult.Fail("expired_token", "The device code has expired.");
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning("Device code exchange failed: authorization denied");
            return OAuthTokenResult.Fail("access_denied", "The authorization request was denied.");
        }

        // Slow-down check per RFC 8628 Section 3.5
        if (entity.LastPolledAt != null
            && (DateTime.UtcNow - entity.LastPolledAt.Value).TotalSeconds < entity.Interval)
        {
            entity.Interval += 5;
            entity.LastPolledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "Device code polling too fast for client {ClientId}, interval increased to {Interval}s",
                clientId,
                entity.Interval
            );
            return OAuthTokenResult.Fail("slow_down", "Polling too frequently. Increase interval.");
        }

        // Update last polled timestamp
        entity.LastPolledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (!entity.IsApproved || entity.GrantId == null)
        {
            return OAuthTokenResult.Fail("authorization_pending", "The authorization request is still pending.");
        }

        // Load the grant with client info
        var grantEntity = await _db.OAuthGrants
            .Include(g => g.Client)
            .FirstOrDefaultAsync(g => g.Id == entity.GrantId, ct);

        if (grantEntity == null || grantEntity.IsRevoked)
        {
            _logger.LogWarning(
                "Device code exchange failed: grant {GrantId} not found or revoked",
                entity.GrantId
            );
            return OAuthTokenResult.Fail("server_error", "Grant not found.");
        }

        var grantInfo = new OAuthGrantInfo
        {
            Id = grantEntity.Id,
            TenantId = grantEntity.TenantId,
            ClientEntityId = grantEntity.ClientEntityId,
            ClientId = grantEntity.Client?.ClientId ?? string.Empty,
            ClientDisplayName = grantEntity.Client?.DisplayName,
            ClientUri = grantEntity.Client?.ClientUri,
            LogoUri = grantEntity.Client?.LogoUri,
            IsKnownClient = grantEntity.Client?.IsKnown ?? false,
            SubjectId = grantEntity.SubjectId,
            GrantType = grantEntity.GrantType,
            Scopes = grantEntity.Scopes,
            Label = grantEntity.Label,
            CreatedAt = grantEntity.CreatedAt,
            LastUsedAt = grantEntity.LastUsedAt,
            LastUsedIp = grantEntity.LastUsedIp,
            LastUsedUserAgent = grantEntity.LastUsedUserAgent,
            IsRevoked = grantEntity.IsRevoked,
        };

        var result = await MintTokenPairAsync(grantInfo, ct);

        await _grantService.UpdateLastUsedAsync(entity.GrantId.Value, null, null, ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} client_id={ClientId} subject_id={SubjectId}",
            "device_code_exchanged", entity.GrantId, clientId, grantEntity.SubjectId
        );

        return result;
    }

    /// <summary>
    /// Mint an access + refresh token pair for a grant.
    /// </summary>
    private async Task<OAuthTokenResult> MintTokenPairAsync(
        OAuthGrantInfo grant,
        CancellationToken ct
    )
    {
        var subject = await _subjectService.GetSubjectByIdAsync(grant.SubjectId);
        if (subject == null)
        {
            return OAuthTokenResult.Fail("server_error", "Subject not found.");
        }

        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
        };

        var roles = await _subjectService.GetSubjectRolesAsync(subject.Id);
        var permissions = ScopeTranslator.ToPermissions(grant.Scopes).ToList();

        var accessToken = _jwtService.GenerateAccessToken(
            subjectInfo,
            permissions,
            roles,
            grant.Scopes,
            grant.ClientId,
            tenantId: grant.TenantId,
            grantId: grant.Id
        );

        // Generate and store refresh token
        var refreshToken = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var refreshTokenEntity = new Infrastructure.Data.Entities.OAuthRefreshTokenEntity
        {
            TenantId = grant.TenantId,
            GrantId = grant.Id,
            TokenHash = tokenHash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        };

        _db.OAuthRefreshTokens.Add(refreshTokenEntity);
        await _db.SaveChangesAsync(ct);

        // Update grant last used timestamp
        await _grantService.UpdateLastUsedAsync(grant.Id, null, null, ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} client_id={ClientId} subject_id={SubjectId} scopes={Scopes}",
            "token_issued", grant.Id, grant.ClientId, grant.SubjectId, string.Join(" ", grant.Scopes));

        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;
        var scope = string.Join(" ", grant.Scopes);

        return OAuthTokenResult.Ok(accessToken, refreshToken, expiresIn, scope);
    }
}
