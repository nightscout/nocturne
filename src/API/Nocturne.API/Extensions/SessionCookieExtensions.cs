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
    /// Append access-token, refresh-token, and IsAuthenticated cookies to the response,
    /// using the centralized <see cref="OidcOptions"/> configuration for names, domain,
    /// path, security flags, and refresh-token lifetime.
    /// </summary>
    public static void SetSessionCookies(
        this HttpResponse response, SessionTokenPair session, OidcOptions options)
    {
        var sameSite = MapSameSiteMode(options.Cookie.SameSite);

        response.Cookies.Append(options.Cookie.AccessTokenName, session.AccessToken, new CookieOptions
        {
            HttpOnly = options.Cookie.HttpOnly,
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.Domain,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds),
        });

        response.Cookies.Append(options.Cookie.RefreshTokenName, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true, // Always HttpOnly for refresh tokens
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.Domain,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.Add(options.Session.RefreshTokenLifetime),
        });

        response.Cookies.Append("IsAuthenticated", "true", new CookieOptions
        {
            HttpOnly = false,
            Secure = options.Cookie.Secure,
            SameSite = sameSite,
            Path = options.Cookie.Path,
            Domain = options.Cookie.Domain,
            Expires = DateTimeOffset.UtcNow.Add(options.Session.RefreshTokenLifetime),
        });
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
