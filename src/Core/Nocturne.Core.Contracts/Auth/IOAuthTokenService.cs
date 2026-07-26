namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Orchestrator service for OAuth token operations.
/// Coordinates authorization code exchange, refresh token rotation, and revocation.
/// </summary>
/// <seealso cref="IOAuthClientService"/>
/// <seealso cref="IOAuthGrantService"/>
/// <seealso cref="IOAuthDeviceCodeService"/>
/// <seealso cref="IRefreshTokenService"/>
/// <seealso cref="IJwtService"/>
public interface IOAuthTokenService
{
    /// <summary>
    /// Exchange an authorization code for access + refresh tokens. The code is claimed atomically
    /// before anything else is validated, so concurrent exchanges of one code yield exactly one
    /// token pair; PKCE, redirect URI and client id are then checked against the claimed code. A
    /// code that is unknown, expired or already redeemed returns a single indistinguishable
    /// <c>invalid_grant</c>, and a replay additionally revokes the grant the code issued.
    /// </summary>
    Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Exchange a refresh token for a new access + refresh token pair.
    /// Implements token rotation with reuse detection.
    /// </summary>
    Task<OAuthTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        string? clientId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Revoke a token. A refresh token is marked revoked in the database; an access token is
    /// blocklisted by its <c>jti</c> for the remainder of its lifetime. The type hint is advisory —
    /// both types are tried. Per RFC 7009, always succeeds, including when nothing matched.
    /// </summary>
    Task RevokeTokenAsync(
        string token,
        string? tokenTypeHint,
        CancellationToken ct = default
    );

    /// <summary>
    /// Exchange a device code for access + refresh tokens (RFC 8628 polling).
    /// Returns authorization_pending, slow_down, expired_token, access_denied, or tokens.
    /// </summary>
    Task<OAuthTokenResult> ExchangeDeviceCodeAsync(
        string deviceCode,
        string clientId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Generate an authorization code for a user who has approved a consent request.
    /// </summary>
    /// <param name="clientEntityId">The OAuth client entity ID</param>
    /// <param name="subjectId">The subject ID who approved the consent</param>
    /// <param name="scopes">The approved scopes</param>
    /// <param name="redirectUri">The redirect URI</param>
    /// <param name="codeChallenge">The PKCE code challenge</param>
    /// <param name="limitTo24Hours">When true, data requests should only return data from the last 24 hours</param>
    /// <param name="ct">Cancellation token</param>
    Task<string> GenerateAuthorizationCodeAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string redirectUri,
        string codeChallenge,
        bool limitTo24Hours = false,
        CancellationToken ct = default
    );
}

/// <summary>
/// Result of a token exchange operation.
/// </summary>
public class OAuthTokenResult
{
    /// <summary>Whether the token exchange succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>The issued access token JWT, present on success.</summary>
    public string? AccessToken { get; set; }

    /// <summary>The issued refresh token, present on success.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Access token lifetime in seconds.</summary>
    public int ExpiresIn { get; set; }

    /// <summary>Space-delimited scope string for the issued tokens, present on success.</summary>
    public string? Scope { get; set; }

    /// <summary>OAuth 2.0 error code, present on failure.</summary>
    public string? Error { get; set; }

    /// <summary>Human-readable error description, present on failure.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>Create a successful token result.</summary>
    public static OAuthTokenResult Ok(
        string accessToken,
        string refreshToken,
        int expiresIn,
        string scope
    ) =>
        new()
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = expiresIn,
            Scope = scope,
        };

    /// <summary>Create a failed token result with an OAuth 2.0 error code and description.</summary>
    public static OAuthTokenResult Fail(string error, string description) =>
        new()
        {
            Success = false,
            Error = error,
            ErrorDescription = description,
        };
}
