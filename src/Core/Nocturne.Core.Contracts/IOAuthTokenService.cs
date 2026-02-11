namespace Nocturne.Core.Contracts;

/// <summary>
/// Orchestrator service for OAuth token operations.
/// Coordinates authorization code exchange, refresh token rotation, and revocation.
/// </summary>
public interface IOAuthTokenService
{
    /// <summary>
    /// Exchange an authorization code for access + refresh tokens.
    /// Validates PKCE, redirect URI, and code expiry.
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
    /// Revoke a token (access or refresh). Per RFC 7009, always succeeds.
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
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public string? Scope { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }

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

    public static OAuthTokenResult Fail(string error, string description) =>
        new()
        {
            Success = false,
            Error = error,
            ErrorDescription = description,
        };
}
