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
    /// Base URL of this application (used for redirect URIs).
    /// If not set, will be auto-detected from the request.
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
    /// State parameter settings for CSRF protection
    /// </summary>
    public StateOptions State { get; set; } = new();

    /// <summary>
    /// Operator-defined providers. When non-empty, bypasses the database entirely
    /// and hides provider management UI.
    /// </summary>
    public List<OidcProviderConfig> Providers { get; set; } = [];
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
    /// Whether to rotate refresh tokens on each use
    /// Improves security but increases database writes
    /// </summary>
    public bool RotateRefreshTokens { get; set; } = true;
}

/// <summary>
/// Cookie configuration settings
/// </summary>
public class CookieSettings
{
    /// <summary>
    /// Name of the OIDC state cookie (for CSRF protection)
    /// </summary>
    public string StateCookieName { get; set; } = ".Nocturne.OidcState";

    /// <summary>
    /// Name of the OIDC link state cookie (for CSRF protection during account linking)
    /// </summary>
    public string LinkStateCookieName { get; set; } = ".Nocturne.OidcLinkState";

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
    /// Name of the platform-admin tenant-access grant cookie. Holds a short-lived,
    /// tenant-pinned JWT that confers out-of-tenant superuser access. Kept separate
    /// from the normal session cookie so it can be issued/cleared independently and so
    /// audit entries can mark the access as out-of-tenant platform access.
    /// </summary>
    public string PlatformAccessName { get; set; } = ".Nocturne.PlatformAccess";
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
