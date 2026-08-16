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
    /// Domain attribute for the platform-access grant cookie, and the operator's override for
    /// <see cref="StateDomain"/>. Defaults, like the other two domains, to ".{base-domain}".
    /// </summary>
    /// <remarks>
    /// Derived rather than left unset because the grant is minted on the apex — where the platform
    /// admin signs in — and redeemed on the <c>{slug}.{base-domain}</c> host it is pinned to. A
    /// host-only cookie is never sent to that second host, so an underived value silently breaks
    /// platform access altogether; anything wider than the base domain broadcasts a superuser
    /// credential past the app. Tenants are always reached at a subdomain of the base domain, so
    /// no deployment topology makes a third value correct. Within it the grant is sent to every
    /// host, including the <c>{token}.share.{base-domain}</c> names served to untrusted share
    /// recipients, which is safe: the cookie is HttpOnly and Secure, and
    /// <c>PlatformAccessCookieHandler</c> skips it unless the resolved tenant is the one it is
    /// pinned to, so it confers nothing on a host belonging to any other tenant.
    /// </remarks>
    public string? Domain { get; set; }

    /// <summary>
    /// Domain attribute for the session cookies (access token, refresh token, IsAuthenticated)
    /// only. Defaults to ".{base-domain}" so a session established on one tenant subdomain is
    /// also presented at the apex dashboard and at sibling tenants the subject belongs to.
    /// </summary>
    public string? SessionDomain { get; set; }

    /// <summary>
    /// Domain attribute for the short-lived OIDC state cookies (login state, link state, and the
    /// setup flow's reuse of the login-state cookie name). Defaults to <see cref="Domain"/>, which
    /// is itself ".{base-domain}" unless the operator overrode it.
    /// </summary>
    /// <remarks>
    /// The registered redirect_uri is the apex callback, so a login begun on any other host has to
    /// present its state cookie at the apex to be verifiable. A host-only state cookie cannot: the
    /// apex never receives it and the callback fails with <c>invalid_state</c>. That is invisible
    /// on a tenant subdomain, where <c>OidcCallbackRedirectMiddleware</c> bounces the callback back
    /// to the originating host before the cookie is read, and fatal on a host that names no tenant
    /// — a reserved dashboard slug — because there is no slug in the state to bounce to.
    /// <para>
    /// Widening is safe: the state cookie is single-use, expires in minutes, and is only ever
    /// compared against the <c>state</c> parameter the provider echoes back, which is itself
    /// integrity-protected. It confers no access on its own.
    /// </para>
    /// </remarks>
    public string? StateDomain { get; set; }

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
