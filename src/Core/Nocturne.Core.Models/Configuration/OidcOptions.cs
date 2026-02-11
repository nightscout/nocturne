namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Configuration options for OIDC authentication
/// </summary>
public class OidcOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "Oidc";

    /// <summary>
    /// Whether OIDC authentication is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of this application (used for redirect URIs)
    /// If not set, will be auto-detected from the request
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Session configuration
    /// </summary>
    public SessionOptions Session { get; set; } = new();

    /// <summary>
    /// Cookie configuration
    /// </summary>
    public CookieSettings Cookie { get; set; } = new();

    /// <summary>
    /// Default return URL after login (if not specified in request)
    /// </summary>
    public string DefaultReturnUrl { get; set; } = "/";

    /// <summary>
    /// Whether to require HTTPS for redirect URIs
    /// Set to false for local development
    /// </summary>
    public bool RequireHttpsRedirect { get; set; } = true;

    /// <summary>
    /// Allowed return URL patterns (for security)
    /// Empty list allows any relative URL
    /// </summary>
    public List<string> AllowedReturnUrlPatterns { get; set; } = new();

    /// <summary>
    /// Allowed URI schemes for native client protocol redirects.
    /// Native clients (e.g. desktop tray apps) use custom protocol schemes
    /// like "nocturne-tray" to receive tokens via protocol activation.
    /// When a return URL uses one of these schemes, the callback appends
    /// tokens as query parameters instead of setting cookies.
    /// </summary>
    public List<string> AllowedNativeSchemes { get; set; } = ["nocturne-tray"];

    /// <summary>
    /// Whether to automatically create subjects from OIDC claims
    /// </summary>
    public bool AutoCreateSubjects { get; set; } = true;

    /// <summary>
    /// State parameter settings for CSRF protection
    /// </summary>
    public StateOptions State { get; set; } = new();
}

/// <summary>
/// Session configuration options
/// </summary>
public class SessionOptions
{
    /// <summary>
    /// Access token lifetime (short-lived for security)
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refresh token lifetime (long-lived for session continuity)
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether to use sliding expiration for refresh tokens
    /// When true, using a refresh token extends its lifetime
    /// </summary>
    public bool SlidingRefreshExpiration { get; set; } = true;

    /// <summary>
    /// Maximum lifetime for refresh tokens even with sliding expiration
    /// </summary>
    public TimeSpan MaxRefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether to rotate refresh tokens on each use
    /// Improves security but increases database writes
    /// </summary>
    public bool RotateRefreshTokens { get; set; } = true;

    /// <summary>
    /// Idle timeout - session expires if no activity for this duration
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>
/// Cookie configuration settings
/// </summary>
public class CookieSettings
{
    /// <summary>
    /// Name of the session cookie
    /// </summary>
    public string Name { get; set; } = ".Nocturne.Session";

    /// <summary>
    /// Name of the OIDC state cookie (for CSRF protection)
    /// </summary>
    public string StateCookieName { get; set; } = ".Nocturne.OidcState";

    /// <summary>
    /// Cookie domain (null = current domain)
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Cookie path
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Whether the cookie requires HTTPS
    /// </summary>
    public bool Secure { get; set; } = true;

    /// <summary>
    /// Whether the cookie is HTTP-only (not accessible via JavaScript)
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// SameSite mode for the cookie
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>
    /// Name of the access token cookie
    /// </summary>
    public string AccessTokenName { get; set; } = ".Nocturne.AccessToken";

    /// <summary>
    /// Name of the refresh token cookie
    /// </summary>
    public string RefreshTokenName { get; set; } = ".Nocturne.RefreshToken";

    /// <summary>
    /// Cookie expiration (should match session lifetime)
    /// </summary>
    public TimeSpan? Expiration { get; set; }
}

/// <summary>
/// State parameter configuration for CSRF protection
/// </summary>
public class StateOptions
{
    /// <summary>
    /// State cookie lifetime
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether to include a nonce for additional security
    /// </summary>
    public bool IncludeNonce { get; set; } = true;
}

/// <summary>
/// SameSite cookie mode
/// </summary>
public enum SameSiteMode
{
    /// <summary>
    /// SameSite=None (requires Secure)
    /// </summary>
    None,

    /// <summary>
    /// SameSite=Lax (allows top-level navigations)
    /// </summary>
    Lax,

    /// <summary>
    /// SameSite=Strict (most restrictive)
    /// </summary>
    Strict,
}
