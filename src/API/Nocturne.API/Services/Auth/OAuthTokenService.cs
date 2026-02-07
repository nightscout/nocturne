using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Orchestrates OAuth token operations: authorization code exchange,
/// refresh token rotation, and revocation.
/// </summary>
public class OAuthTokenService : IOAuthTokenService
{
    private readonly NocturneDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly ISubjectService _subjectService;
    private readonly IOAuthGrantService _grantService;
    private readonly ILogger<OAuthTokenService> _logger;

    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(10);

    public OAuthTokenService(
        NocturneDbContext db,
        IJwtService jwtService,
        ISubjectService subjectService,
        IOAuthGrantService grantService,
        ILogger<OAuthTokenService> logger
    )
    {
        _db = db;
        _jwtService = jwtService;
        _subjectService = subjectService;
        _grantService = grantService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAuthorizationCodeAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string redirectUri,
        string codeChallenge,
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
        };

        _db.OAuthAuthorizationCodes.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Generated authorization code for client entity {ClientEntityId}, subject {SubjectId}",
            clientEntityId,
            subjectId
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

        var authCode = await _db.OAuthAuthorizationCodes
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

        if (authCode == null)
        {
            _logger.LogWarning("Authorization code exchange failed: code not found");
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code is invalid.");
        }

        // Check not expired
        if (authCode.IsExpired)
        {
            _logger.LogWarning("Authorization code exchange failed: code expired");
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code has expired.");
        }

        // Check not already redeemed (prevents replay)
        if (authCode.IsRedeemed)
        {
            _logger.LogWarning(
                "Authorization code replay detected for subject {SubjectId}",
                authCode.SubjectId
            );
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code has already been used.");
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

        // Mark as redeemed
        authCode.RedeemedAt = DateTime.UtcNow;

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
            GrantId = oauthToken.GrantId,
            TokenHash = newTokenHash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        };

        _db.OAuthRefreshTokens.Add(newTokenEntity);
        await _db.SaveChangesAsync(ct);

        // Set the rotation chain link
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
            grant.Client?.ClientId
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
        var tokenHash = _jwtService.HashRefreshToken(token);

        // Try as refresh token first (or if hinted)
        if (tokenTypeHint is null or "refresh_token")
        {
            var refreshToken = await _db.OAuthRefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.RevokedAt == null, ct);

            if (refreshToken != null)
            {
                refreshToken.RevokedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "OAuthAudit: {Event} token_id={TokenId} grant_id={GrantId}",
                    "refresh_token_revoked", refreshToken.Id, refreshToken.GrantId);
                return;
            }
        }

        // Per RFC 7009: always return success even if token not found
        _logger.LogDebug("Token revocation: token not found (this is normal per RFC 7009)");
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
            ClientEntityId = grantEntity.ClientEntityId,
            ClientId = grantEntity.Client?.ClientId ?? string.Empty,
            ClientDisplayName = grantEntity.Client?.DisplayName,
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
            grant.ClientId
        );

        // Generate and store refresh token
        var refreshToken = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var refreshTokenEntity = new Infrastructure.Data.Entities.OAuthRefreshTokenEntity
        {
            GrantId = grant.Id,
            TokenHash = tokenHash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        };

        _db.OAuthRefreshTokens.Add(refreshTokenEntity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} client_id={ClientId} subject_id={SubjectId} scopes={Scopes}",
            "token_issued", grant.Id, grant.ClientId, grant.SubjectId, string.Join(" ", grant.Scopes));

        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;
        var scope = string.Join(" ", grant.Scopes);

        return OAuthTokenResult.Ok(accessToken, refreshToken, expiresIn, scope);
    }
}
