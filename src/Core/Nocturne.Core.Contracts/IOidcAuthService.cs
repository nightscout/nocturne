using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for handling OIDC authentication flows
/// </summary>
public interface IOidcAuthService
{
    /// <summary>
    /// Generate an authorization URL for initiating OIDC login
    /// </summary>
    /// <param name="providerId">OIDC provider ID (null = use default)</param>
    /// <param name="returnUrl">URL to return to after login</param>
    /// <param name="state">State parameter for CSRF protection (generated if null)</param>
    /// <returns>Authorization request containing URL and state</returns>
    /// <exception cref="InvalidOperationException">Thrown when the provider is not found, not configured, or not enabled.</exception>
    Task<OidcAuthorizationRequest> GenerateAuthorizationUrlAsync(
        Guid? providerId,
        string? returnUrl = null,
        string? state = null
    );

    /// <summary>
    /// Handle the OIDC callback - exchange code for tokens and create session
    /// </summary>
    /// <param name="code">Authorization code from provider</param>
    /// <param name="state">State parameter for CSRF verification</param>
    /// <param name="expectedState">Expected state value from cookie</param>
    /// <param name="ipAddress">Client IP address</param>
    /// <param name="userAgent">User agent string</param>
    /// <returns>Authentication result with session tokens</returns>
    Task<OidcCallbackResult> HandleCallbackAsync(
        string code,
        string state,
        string expectedState,
        string? ipAddress = null,
        string? userAgent = null
    );

    /// <summary>
    /// Refresh the session using a refresh token
    /// </summary>
    /// <param name="refreshToken">Current refresh token</param>
    /// <param name="ipAddress">Client IP address</param>
    /// <param name="userAgent">User agent string</param>
    /// <returns>New session tokens or null if refresh failed</returns>
    Task<OidcTokenResponse?> RefreshSessionAsync(
        string refreshToken,
        string? ipAddress = null,
        string? userAgent = null
    );

    /// <summary>
    /// End the session (logout)
    /// </summary>
    /// <param name="refreshToken">Refresh token to revoke</param>
    /// <param name="providerId">Provider ID for RP-initiated logout (optional)</param>
    /// <returns>Logout result with optional provider logout URL</returns>
    Task<OidcLogoutResult> LogoutAsync(string refreshToken, Guid? providerId = null);

    /// <summary>
    /// Get user information from the current session
    /// </summary>
    /// <param name="subjectId">Subject ID</param>
    /// <returns>User info or null if not found</returns>
    Task<OidcUserInfo?> GetUserInfoAsync(Guid subjectId);

    /// <summary>
    /// Validate a session (check if refresh token is valid)
    /// </summary>
    /// <param name="refreshToken">Refresh token to validate</param>
    /// <returns>Subject ID if valid, null otherwise</returns>
    Task<Guid?> ValidateSessionAsync(string refreshToken);
}

/// <summary>
/// OIDC authorization request
/// </summary>
public class OidcAuthorizationRequest
{
    /// <summary>
    /// Full authorization URL to redirect to
    /// </summary>
    public string AuthorizationUrl { get; set; } = string.Empty;

    /// <summary>
    /// State parameter (should be stored in cookie for verification)
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Nonce value (for ID token verification)
    /// </summary>
    public string? Nonce { get; set; }

    /// <summary>
    /// Provider ID
    /// </summary>
    public Guid ProviderId { get; set; }

    /// <summary>
    /// Return URL after authentication
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// State expiration time
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// OIDC callback result
/// </summary>
public class OidcCallbackResult
{
    /// <summary>
    /// Whether the callback was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Error description if failed
    /// </summary>
    public string? ErrorDescription { get; set; }

    /// <summary>
    /// Session tokens if successful
    /// </summary>
    public OidcTokenResponse? Tokens { get; set; }

    /// <summary>
    /// User information if successful
    /// </summary>
    public OidcUserInfo? UserInfo { get; set; }

    /// <summary>
    /// Return URL extracted from state
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static OidcCallbackResult Succeeded(
        OidcTokenResponse tokens,
        OidcUserInfo userInfo,
        string? returnUrl = null
    ) =>
        new()
        {
            Success = true,
            Tokens = tokens,
            UserInfo = userInfo,
            ReturnUrl = returnUrl,
        };

    /// <summary>
    /// Create a failed result
    /// </summary>
    public static OidcCallbackResult Failed(string error, string? description = null) =>
        new()
        {
            Success = false,
            Error = error,
            ErrorDescription = description,
        };
}

/// <summary>
/// OIDC token response (our session tokens, not provider tokens)
/// </summary>
public class OidcTokenResponse
{
    /// <summary>
    /// Access token (short-lived JWT)
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token (long-lived, for session continuity)
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Token type (always "Bearer")
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// Access token expiration in seconds
    /// </summary>
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Absolute expiration time
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Subject ID
    /// </summary>
    public Guid SubjectId { get; set; }
}

/// <summary>
/// OIDC user information
/// </summary>
public class OidcUserInfo
{
    /// <summary>
    /// Subject ID
    /// </summary>
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Email address
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Whether email is verified
    /// </summary>
    public bool? EmailVerified { get; set; }

    /// <summary>
    /// Picture URL
    /// </summary>
    public string? Picture { get; set; }

    /// <summary>
    /// Assigned roles
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// Resolved permissions
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// OIDC provider name
    /// </summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Last login time
    /// </summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }
}

/// <summary>
/// OIDC logout result
/// </summary>
public class OidcLogoutResult
{
    /// <summary>
    /// Whether the logout was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// URL for RP-initiated logout at the provider (optional)
    /// </summary>
    public string? ProviderLogoutUrl { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static OidcLogoutResult Succeeded(string? providerLogoutUrl = null) =>
        new() { Success = true, ProviderLogoutUrl = providerLogoutUrl };

    /// <summary>
    /// Create a failed result
    /// </summary>
    public static OidcLogoutResult Failed(string error) => new() { Success = false, Error = error };
}
