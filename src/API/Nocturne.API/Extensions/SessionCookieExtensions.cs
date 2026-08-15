using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using ConfigSameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Extensions;

/// <summary>
/// Shared cookie-writing logic for session token pairs issued by any auth flow.
/// </summary>
public static class SessionCookieExtensions
{
    /// <summary>
    /// The non-HttpOnly flag cookie the frontend reads to know a session exists.
    /// </summary>
    public const string IsAuthenticatedCookieName = "IsAuthenticated";

    /// <summary>
    /// Derive the session-cookie <c>Domain</c> attribute from the configured base domain, so a
    /// session established on one tenant subdomain is carried to the apex dashboard and to sibling
    /// tenants the subject belongs to. Returns null (host-only cookies) when widening is unsafe.
    /// </summary>
    /// <remarks>
    /// Two cases yield null. A single-label host ("localhost") cannot carry a Domain attribute at
    /// all — browsers reject it. And Chromium does not reliably scope cookies across
    /// <c>*.localhost</c> names, so local development keeps host-only cookies rather than silently
    /// dropping every session cookie; dev works per-host as it always has.
    /// </remarks>
    public static string? ResolveCookieDomain(string? baseDomain)
    {
        // A Domain attribute is a bare hostname: it carries no port, and a value with one is
        // discarded wholesale by the browser (taking the session with it).
        var (host, _) = BaseDomainOptions.SplitHostPort(baseDomain ?? "");
        if (string.IsNullOrEmpty(host))
            return null;

        if (!host.Contains('.'))
            return null;

        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return null;

        return $".{host}";
    }

    /// <summary>
    /// Append access-token, refresh-token, and IsAuthenticated cookies to the response,
    /// using the centralized <see cref="OidcOptions"/> configuration for names, domain,
    /// path, security flags, and refresh-token lifetime.
    /// </summary>
    public static void SetSessionCookies(
        this HttpResponse response, SessionTokenPair session, OidcOptions options) =>
        response.SetSessionCookies(
            session.AccessToken,
            session.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds),
            options);

    /// <summary>
    /// The single session-cookie writer. Every auth flow (OIDC callback, passkey, TOTP, setup,
    /// demo, dev, and the silent refresh in <c>SessionCookieHandler</c>) funnels through here so
    /// the cookie attributes — above all <c>Domain</c> — cannot drift between them.
    /// </summary>
    public static void SetSessionCookies(
        this HttpResponse response,
        string accessToken,
        string refreshToken,
        DateTimeOffset accessTokenExpiresAt,
        OidcOptions options)
    {
        var sameSite = MapSameSiteMode(options.Cookie.SameSite);
        var refreshExpiresAt = DateTimeOffset.UtcNow.Add(options.Session.RefreshTokenLifetime);

        // Expire any host-scoped cookie of the same name before writing the domain-wide one.
        // A client that authenticated before the domain was widened holds both, and the browser
        // sends both under one Cookie header with no way for the server to tell them apart, so
        // whichever the server reads is arbitrary. Clearing the narrow variant first makes every
        // client converge on the domain-wide cookie after a single authenticated response.
        // Order matters: Response.Cookies.Delete strips already-appended Set-Cookie headers that
        // match by path, so deleting after appending would cancel the write we just made.
        ExpireHostScopedSessionCookies(response, options);

        response.Cookies.Append(options.Cookie.AccessTokenName, accessToken, new CookieOptions
        {
            HttpOnly = options.Cookie.HttpOnly,
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.SessionDomain,
            IsEssential = true,
            Expires = accessTokenExpiresAt,
        });

        response.Cookies.Append(options.Cookie.RefreshTokenName, refreshToken, new CookieOptions
        {
            HttpOnly = true, // Always HttpOnly for refresh tokens
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.SessionDomain,
            IsEssential = true,
            Expires = refreshExpiresAt,
        });

        response.Cookies.Append(IsAuthenticatedCookieName, "true", new CookieOptions
        {
            HttpOnly = false,
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.SessionDomain,
            Expires = refreshExpiresAt,
        });
    }

    /// <summary>
    /// Delete the session cookies, mirroring <see cref="SetSessionCookies(HttpResponse, string, string, DateTimeOffset, OidcOptions)"/>.
    /// A cookie is keyed by name, domain, and path, so sign-out must present the same
    /// <c>Domain</c> the write used or the browser keeps the session cookie.
    /// </summary>
    public static void ClearSessionCookies(this HttpResponse response, OidcOptions options)
    {
        // Sign-out must also drop the pre-widening host-scoped cookies, or the stale narrow
        // cookie survives and re-authenticates the "signed-out" browser. First, for the same
        // header-stripping reason as in the writer.
        ExpireHostScopedSessionCookies(response, options);

        var cookieOptions = new CookieOptions
        {
            Path = options.Cookie.Path,
            Domain = options.Cookie.SessionDomain,
        };

        response.Cookies.Delete(options.Cookie.AccessTokenName, cookieOptions);
        response.Cookies.Delete(options.Cookie.RefreshTokenName, cookieOptions);
        response.Cookies.Delete(IsAuthenticatedCookieName, cookieOptions);
    }

    /// <summary>
    /// Expire the host-only variants of the session cookies, i.e. the same names written without a
    /// <c>Domain</c> attribute. No-op when cookies are already host-scoped, which would otherwise
    /// emit an expiry for the very cookie being written.
    /// </summary>
    private static void ExpireHostScopedSessionCookies(HttpResponse response, OidcOptions options)
    {
        if (string.IsNullOrEmpty(options.Cookie.SessionDomain))
            return;

        var hostScoped = new CookieOptions { Path = options.Cookie.Path };

        response.Cookies.Delete(options.Cookie.AccessTokenName, hostScoped);
        response.Cookies.Delete(options.Cookie.RefreshTokenName, hostScoped);
        response.Cookies.Delete(IsAuthenticatedCookieName, hostScoped);
    }

    internal static Microsoft.AspNetCore.Http.SameSiteMode MapSameSiteMode(
        ConfigSameSiteMode mode) => mode switch
    {
        ConfigSameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
        ConfigSameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        ConfigSameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
    };
}
