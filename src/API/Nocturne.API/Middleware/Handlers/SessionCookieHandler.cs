using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for session cookies set by OidcController or LocalAuthController.
/// Validates the access token JWT stored in the session cookie.
/// Falls back to refresh token if access token is expired.
/// </summary>
public class SessionCookieHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (50 - highest priority, before OIDC tokens)
    /// Session cookies should be checked first for web app authentication
    /// </summary>
    public int Priority => 50;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "SessionCookieHandler";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCookieHandler> _logger;
    private readonly OidcOptions _options;
    private readonly LocalIdentityOptions _localIdentityOptions;

    /// <summary>
    /// Creates a new instance of SessionCookieHandler
    /// </summary>
    public SessionCookieHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionCookieHandler> logger,
        IOptions<OidcOptions> options,
        IOptions<LocalIdentityOptions> localIdentityOptions
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _localIdentityOptions = localIdentityOptions.Value;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // Skip if neither OIDC nor LocalIdentity is enabled
        if (!_options.Enabled && !_localIdentityOptions.Enabled)
        {
            _logger.LogInformation("[SessionCookieHandler] Both OIDC and LocalIdentity are disabled, skipping");
            return AuthResult.Skip();
        }

        // Check for access token in session cookie
        var accessToken = context.Request.Cookies[_options.Cookie.AccessTokenName];
        _logger.LogInformation("[SessionCookieHandler] Looking for cookie '{CookieName}': {Found}",
            _options.Cookie.AccessTokenName,
            !string.IsNullOrEmpty(accessToken) ? "Found (" + accessToken.Length + " chars)" : "NOT FOUND");

        // Log all cookies received for debugging
        var allCookies = context.Request.Cookies.Keys;
        _logger.LogInformation("[SessionCookieHandler] All cookies received: {Cookies}", string.Join(", ", allCookies));

        using var scope = _scopeFactory.CreateScope();

        if (!string.IsNullOrEmpty(accessToken))
        {
            // Try to validate the access token
            var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();

            var validationResult = jwtService.ValidateAccessToken(accessToken);

            if (validationResult.IsValid && validationResult.Claims != null)
            {

                return AuthResult.Success(
                    BuildAuthContextFromClaims(validationResult.Claims, accessToken)
                );
            }

            // Access token invalid - try to refresh using refresh token
            // We attempt refresh for any validation error, not just expiry,
            // as the token may have been invalidated for various reasons
            var refreshResult = await TryRefreshSessionAsync(context, scope);
            if (refreshResult != null)
            {
                return refreshResult;
            }

            // Only clear cookies if we had an access token but refresh failed
            // This indicates a definitive auth failure, not just "skip to next handler"
            _logger.LogDebug("Access token validation failed ({Error}) and refresh failed, clearing session cookies",
                validationResult.ErrorCode);
            ClearSessionCookies(context);
            return AuthResult.Skip();
        }

        // No access token - check if we have a refresh token we can use
        var refreshToken = context.Request.Cookies[_options.Cookie.RefreshTokenName];
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var refreshResult = await TryRefreshSessionAsync(context, scope);
            if (refreshResult != null)
            {
                return refreshResult;
            }

            // Had refresh token but it failed - clear cookies and skip
            _logger.LogDebug("No access token and refresh token validation failed, clearing session cookies");
            ClearSessionCookies(context);
        }
        // No cookies at all - just skip to next handler without clearing anything

        return AuthResult.Skip();
    }

    /// <summary>
    /// Try to refresh the session using the refresh token
    /// </summary>
    private async Task<AuthResult?> TryRefreshSessionAsync(HttpContext context, IServiceScope scope)
    {
        var refreshToken = context.Request.Cookies[_options.Cookie.RefreshTokenName];

        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        try
        {
            var authService = scope.ServiceProvider.GetRequiredService<IOidcAuthService>();
            var ipAddress = GetClientIpAddress(context);
            var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();

            var newTokens = await authService.RefreshSessionAsync(
                refreshToken,
                ipAddress,
                userAgent
            );

            if (newTokens == null)
            {
                ClearSessionCookies(context);
                return null;
            }

            // Set new cookies in response
            SetSessionCookies(context, newTokens);

            // Validate the new access token to get claims
            var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();
            var validationResult = jwtService.ValidateAccessToken(newTokens.AccessToken);

            if (validationResult.IsValid && validationResult.Claims != null)
            {

                return AuthResult.Success(
                    BuildAuthContextFromClaims(validationResult.Claims, newTokens.AccessToken)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing session");
        }

        return null;
    }

    /// <summary>
    /// Build an AuthContext from JWT claims
    /// </summary>
    private static AuthContext BuildAuthContextFromClaims(JwtClaims claims, string token)
    {
        return new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = claims.SubjectId,
            SubjectName = claims.Name,
            Email = claims.Email,
            Roles = claims.Roles,
            Permissions = claims.Permissions,
            RawToken = token,
            ExpiresAt = claims.ExpiresAt,
        };
    }

    /// <summary>
    /// Set session cookies in the response
    /// </summary>
    private void SetSessionCookies(HttpContext context, OidcTokenResponse tokens)
    {
        // Access token cookie (short-lived)
        context.Response.Cookies.Append(
            _options.Cookie.AccessTokenName,
            tokens.AccessToken,
            new CookieOptions
            {
                HttpOnly = _options.Cookie.HttpOnly,
                Secure = _options.Cookie.Secure,
                SameSite = MapSameSiteMode(_options.Cookie.SameSite),
                Path = _options.Cookie.Path,
                Domain = _options.Cookie.Domain,
                Expires = tokens.ExpiresAt,
            }
        );

        // Refresh token cookie (longer-lived)
        context.Response.Cookies.Append(
            _options.Cookie.RefreshTokenName,
            tokens.RefreshToken,
            new CookieOptions
            {
                HttpOnly = true, // Always HttpOnly for refresh tokens
                Secure = _options.Cookie.Secure,
                SameSite = MapSameSiteMode(_options.Cookie.SameSite),
                Path = _options.Cookie.Path,
                Domain = _options.Cookie.Domain,
                Expires = DateTimeOffset.UtcNow.Add(_options.Session.RefreshTokenLifetime),
            }
        );

        // Also update the non-HttpOnly IsAuthenticated cookie for frontend state tracking
        context.Response.Cookies.Append(
            "IsAuthenticated",
            "true",
            new CookieOptions
            {
                HttpOnly = false,
                Secure = _options.Cookie.Secure,
                SameSite = MapSameSiteMode(_options.Cookie.SameSite),
                Path = "/",
                Domain = _options.Cookie.Domain,
                Expires = DateTimeOffset.UtcNow.Add(_options.Session.RefreshTokenLifetime),
            }
        );
    }

    /// <summary>
    /// Clear session cookies
    /// </summary>
    private void ClearSessionCookies(HttpContext context)
    {
        var cookieOptions = new CookieOptions
        {
            Path = _options.Cookie.Path,
            Domain = _options.Cookie.Domain,
        };

        context.Response.Cookies.Delete(_options.Cookie.AccessTokenName, cookieOptions);
        context.Response.Cookies.Delete(_options.Cookie.RefreshTokenName, cookieOptions);
        context.Response.Cookies.Delete("IsAuthenticated", cookieOptions);
    }

    /// <summary>
    /// Get client IP address
    /// </summary>
    private static string? GetClientIpAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            return forwarded.Split(',').First().Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Map SameSite mode
    /// </summary>
    private static Microsoft.AspNetCore.Http.SameSiteMode MapSameSiteMode(
        SameSiteMode mode
    )
    {
        return mode switch
        {
            SameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
            SameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            SameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        };
    }
}
