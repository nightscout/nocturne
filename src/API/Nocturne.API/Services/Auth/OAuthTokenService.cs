using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;

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
    private readonly IOAuthTokenRepository _tokens;
    private readonly IOAuthGrantRepository _grants;
    private readonly IJwtService _jwtService;
    private readonly ISubjectService _subjectService;
    private readonly IOAuthGrantService _grantService;
    private readonly IOAuthTokenRevocationCache _revocationCache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OAuthTokenService> _logger;

    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a rotated refresh token stays redeemable. A client that sends parallel requests,
    /// each refreshing on its 401 with the same token, must not lose its grant to its own race, so
    /// within this window a rotated token may mint up to <see cref="MaxGraceRedemptions"/> sibling
    /// successors (Okta's grace period, Auth0's reuse interval). A sibling starts a chain the
    /// rotation link does not record, so the cap bounds how far a copied token can fork. Any other
    /// presentation of a rotated token is reuse and revokes the grant.
    /// </summary>
    private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(10);

    /// <inheritdoc cref="RefreshReuseGrace"/>
    private const int MaxGraceRedemptions = 1;

    /// <param name="tokens">The stored codes and refresh tokens.</param>
    /// <param name="grants">The stored grants, for <see cref="LoadGrantAsync"/>.</param>
    /// <param name="jwtService">Service for generating and validating JWT access tokens and refresh tokens.</param>
    /// <param name="subjectService">Service for resolving subject permissions and roles for token claims.</param>
    /// <param name="grantService">Service for persisting and querying OAuth consent grants.</param>
    /// <param name="revocationCache">Blocklist of revoked access tokens, keyed by <c>jti</c>.</param>
    /// <param name="timeProvider">The clock every lifetime and claim is stamped by.</param>
    /// <param name="logger">The logger.</param>
    public OAuthTokenService(
        IOAuthTokenRepository tokens,
        IOAuthGrantRepository grants,
        IJwtService jwtService,
        ISubjectService subjectService,
        IOAuthGrantService grantService,
        IOAuthTokenRevocationCache revocationCache,
        TimeProvider timeProvider,
        ILogger<OAuthTokenService> logger
    )
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _jwtService = jwtService;
        _subjectService = subjectService;
        _grantService = grantService;
        _revocationCache = revocationCache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

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

        await _tokens.StoreAuthorizationCodeAsync(
            new NewAuthorizationCode(
                clientEntityId,
                subjectId,
                codeHash,
                scopes.ToList(),
                redirectUri,
                codeChallenge,
                UtcNow.Add(AuthorizationCodeLifetime),
                limitTo24Hours),
            ct);

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

        // Claimed before anything else is checked, so a racing exchange of the same code fails here
        // rather than after both have passed the checks.
        if (!await _tokens.TryClaimAuthorizationCodeAsync(codeHash, UtcNow, ct))
        {
            // Unknown, expired and already-redeemed are one error: RFC 6749 Section 5.2 maps all
            // three to invalid_grant, and a single message tells a caller nothing about which code
            // hashes exist. On a genuine replay the tokens already issued from the code are revoked
            // (RFC 6749 Section 4.1.2, OAuth 2.0 Security BCP Section 4.1.2).
            await RevokeGrantOnCodeReplayAsync(codeHash, ct);
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code is invalid.");
        }

        var authCode = await _tokens.FindAuthorizationCodeAsync(codeHash, ct);

        if (authCode == null)
        {
            // The row was claimed a moment ago, so this only happens if it was deleted meanwhile.
            _logger.LogWarning("Authorization code exchange failed: claimed code no longer readable");
            return OAuthTokenResult.Fail("invalid_grant", "Authorization code is invalid.");
        }

        // Verify client_id matches
        if (authCode.ClientId != clientId)
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
            limitTo24Hours: authCode.LimitTo24Hours,
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
        var replayed = await _tokens.FindAuthorizationCodeAsync(codeHash, ct);

        if (replayed is not { RedeemedAt: not null })
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

        var oauthToken = await _tokens.FindRefreshTokenAsync(tokenHash, ct);

        if (oauthToken == null)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Refresh token is invalid.");
        }

        if (oauthToken.IsRevoked && !IsRotatedWithinGrace(oauthToken))
        {
            return await RefuseRevokedAsync(oauthToken, ct);
        }

        if (UtcNow >= oauthToken.ExpiresAt)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Refresh token has expired.");
        }

        // Verify grant is still active
        if (oauthToken.Grant == null || oauthToken.Grant.IsRevoked)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Authorization grant has been revoked.");
        }

        // Verify client_id if provided
        if (!string.IsNullOrEmpty(clientId) && oauthToken.Grant.ClientId != clientId)
        {
            return OAuthTokenResult.Fail("invalid_grant", "Client ID does not match.");
        }

        if (oauthToken.IsRevoked && !await TryRedeemWithinGraceAsync(oauthToken, ct))
        {
            return RevokedTokenFailure();
        }

        var newRefreshToken = _jwtService.GenerateRefreshToken();
        var successorId = await _tokens.StoreRefreshTokenAsync(
            new NewOAuthRefreshToken(
                oauthToken.Grant.TenantId,
                oauthToken.GrantId,
                _jwtService.HashRefreshToken(newRefreshToken),
                UtcNow.AddDays(90)),
            ct);

        // The successor exists before the claim links it, so the claim never leaves the token
        // revoked without its replacement (see IOAuthTokenRepository.TryRotateRefreshTokenAsync).
        if (!oauthToken.IsRevoked
            && !await _tokens.TryRotateRefreshTokenAsync(oauthToken.Id, successorId, UtcNow, ct))
        {
            var winner = await _tokens.GetRefreshTokenAsync(oauthToken.Id, ct);

            var refusal = winner is null ? RevokedTokenFailure()
                : !IsRotatedWithinGrace(winner) ? await RefuseRevokedAsync(winner, ct)
                : await TryRedeemWithinGraceAsync(winner, ct) ? null
                : RevokedTokenFailure();

            if (refusal is not null)
            {
                await _tokens.RevokeRefreshTokenAsync(successorId, UtcNow, ct);
                return refusal;
            }
        }

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
            grant.ClientId,
            limitTo24Hours: grant.LimitTo24Hours,
            tenantId: grant.TenantId,
            grantId: grant.Id
        );

        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;
        var scopeStr = string.Join(" ", grant.Scopes);

        return OAuthTokenResult.Ok(accessToken, newRefreshToken, expiresIn, scopeStr);
    }

    private static OAuthTokenResult RevokedTokenFailure() =>
        OAuthTokenResult.Fail("invalid_grant", "Refresh token has been revoked.");

    /// <summary>
    /// Whether <paramref name="token"/> was rotated, not revoked, within <see cref="RefreshReuseGrace"/>.
    /// </summary>
    private bool IsRotatedWithinGrace(OAuthRefreshTokenRecord token) =>
        token.ReplacedById != null && token.RevokedAt >= UtcNow - RefreshReuseGrace;

    /// <summary>
    /// Refuses a revoked token presented outside <see cref="RefreshReuseGrace"/>. A rotated one is
    /// reuse and revokes the grant; one revoked without rotation is simply refused.
    /// </summary>
    private async Task<OAuthTokenResult> RefuseRevokedAsync(OAuthRefreshTokenRecord token, CancellationToken ct)
    {
        if (token.ReplacedById != null)
        {
            await RevokeGrantOnRefreshReuseAsync(token.GrantId, ct);
        }

        return RevokedTokenFailure();
    }

    /// <summary>
    /// Claims one of a rotated token's <see cref="MaxGraceRedemptions"/>. Past the cap the
    /// presentation is reuse and revokes the grant. A successor signed out by revocation is not
    /// undone, so the token is refused without a claim.
    /// </summary>
    private async Task<bool> TryRedeemWithinGraceAsync(OAuthRefreshTokenRecord token, CancellationToken ct)
    {
        if (token.ReplacedById is { } successor && await _tokens.IsRefreshTokenSignedOutAsync(successor, ct))
        {
            return false;
        }

        if (!await _tokens.TryRedeemRefreshTokenGraceAsync(token.Id, MaxGraceRedemptions, ct))
        {
            await RevokeGrantOnRefreshReuseAsync(token.GrantId, ct);
            return false;
        }

        _logger.LogWarning(
            "Rotated refresh token {TokenId} redeemed inside the reuse grace window for grant {GrantId}",
            token.Id, token.GrantId);
        return true;
    }

    /// <summary>
    /// Revokes the grant behind a reused refresh token. Revoking the grant also revokes every
    /// refresh token it issued.
    /// </summary>
    private async Task RevokeGrantOnRefreshReuseAsync(Guid grantId, CancellationToken ct)
    {
        _logger.LogWarning(
            "Refresh token reuse detected for grant {GrantId}. Revoking entire grant.", grantId);
        await _grantService.RevokeGrantAsync(grantId, ct);
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
    /// Revokes <paramref name="token"/> if it is a live stored refresh token. Returns whether it was.
    /// </summary>
    private async Task<bool> RevokeAsRefreshTokenAsync(string token, CancellationToken ct)
    {
        var tokenHash = _jwtService.HashRefreshToken(token);

        var refreshToken = await _tokens.FindRefreshTokenAsync(tokenHash, ct);

        if (refreshToken is null || refreshToken.IsRevoked)
        {
            return false;
        }

        if (!await _tokens.RevokeRefreshTokenAsync(refreshToken.Id, UtcNow, ct))
        {
            return false;
        }

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

        var remainingLifetime = claims.ExpiresAt - _timeProvider.GetUtcNow();
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

        var entity = await _tokens.FindDeviceCodeAsync(codeHash, ct);

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

        if (UtcNow >= entity.ExpiresAt)
        {
            _logger.LogWarning("Device code exchange failed: code expired");
            return OAuthTokenResult.Fail("expired_token", "The device code has expired.");
        }

        if (entity.RedeemedAt != null)
        {
            await RevokeGrantOnDeviceCodeReplayAsync(entity.GrantId, ct);
            return OAuthTokenResult.Fail("invalid_grant", "Device code is invalid.");
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning("Device code exchange failed: authorization denied");
            return OAuthTokenResult.Fail("access_denied", "The authorization request was denied.");
        }

        // Slow-down check per RFC 8628 Section 3.5
        if (entity.LastPolledAt != null
            && (UtcNow - entity.LastPolledAt.Value).TotalSeconds < entity.Interval)
        {
            await _tokens.BumpDeviceCodeIntervalAsync(entity.Id, 5, ct);
            await _tokens.RecordDevicePollAsync(entity.Id, UtcNow, ct);

            _logger.LogDebug(
                "Device code polling too fast for client {ClientId}, interval increased to at least {Interval}s",
                clientId,
                entity.Interval + 5
            );
            return OAuthTokenResult.Fail("slow_down", "Polling too frequently. Increase interval.");
        }

        await _tokens.RecordDevicePollAsync(entity.Id, UtcNow, ct);

        if (!entity.IsApproved || entity.GrantId == null)
        {
            return OAuthTokenResult.Fail("authorization_pending", "The authorization request is still pending.");
        }

        if (!await _tokens.TryRedeemDeviceCodeAsync(entity.Id, UtcNow, ct))
        {
            await RevokeGrantOnDeviceCodeReplayAsync(entity.GrantId, ct);
            return OAuthTokenResult.Fail("invalid_grant", "Device code is invalid.");
        }

        var grantInfo = await LoadGrantAsync(entity.GrantId.Value, ct);

        if (grantInfo == null)
        {
            _logger.LogWarning(
                "Device code exchange failed: grant {GrantId} not found or revoked",
                entity.GrantId
            );
            return OAuthTokenResult.Fail("server_error", "Grant not found.");
        }

        var result = await MintTokenPairAsync(grantInfo, ct);

        await _grantService.UpdateLastUsedAsync(entity.GrantId.Value, null, null, ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} client_id={ClientId} subject_id={SubjectId}",
            "device_code_exchanged", entity.GrantId, clientId, grantInfo.SubjectId
        );

        return result;
    }

    /// <summary>
    /// The grant a device code was approved into, with its client; null when it is gone or
    /// revoked.
    /// </summary>
    private async Task<OAuthGrantInfo?> LoadGrantAsync(Guid grantId, CancellationToken ct)
    {
        var grant = await _grants.GetAsync(grantId, ct);
        return grant is null || grant.IsRevoked ? null : OAuthGrantService.ToInfo(grant);
    }

    /// <summary>
    /// Revokes the grant issued from a device code that was presented again after it was
    /// exchanged, for the reason <see cref="RevokeGrantOnCodeReplayAsync"/> gives.
    /// </summary>
    private async Task RevokeGrantOnDeviceCodeReplayAsync(Guid? grantId, CancellationToken ct)
    {
        _logger.LogWarning("Device code replay detected for grant {GrantId}", grantId);
        if (grantId != null)
        {
            await _grantService.RevokeGrantAsync(grantId.Value, ct);
        }
    }

    /// <summary>
    /// Mint an access + refresh token pair for a grant.
    /// </summary>
    private async Task<OAuthTokenResult> MintTokenPairAsync(OAuthGrantInfo grant, CancellationToken ct)
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
            limitTo24Hours: grant.LimitTo24Hours,
            tenantId: grant.TenantId,
            grantId: grant.Id
        );

        // Generate and store refresh token
        var refreshToken = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        await _tokens.StoreRefreshTokenAsync(
            new NewOAuthRefreshToken(grant.TenantId, grant.Id, tokenHash, UtcNow.AddDays(90)), ct);

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
