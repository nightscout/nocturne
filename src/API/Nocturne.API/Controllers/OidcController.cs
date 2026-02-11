using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for OIDC authentication flows
/// Handles login initiation, OAuth callback, logout, and session management
/// </summary>
[ApiController]
[Route("auth")]
[Tags("Oidc")]
public class OidcController : ControllerBase
{
    private readonly IOidcAuthService _authService;
    private readonly IOidcProviderService _providerService;
    private readonly OidcOptions _options;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Creates a new instance of OidcController
    /// </summary>
    public OidcController(
        IOidcAuthService authService,
        IOidcProviderService providerService,
        IOptions<OidcOptions> options,
        ILogger<OidcController> logger
    )
    {
        _authService = authService;
        _providerService = providerService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get available OIDC providers for login
    /// </summary>
    /// <returns>List of enabled providers</returns>
    [HttpGet("providers")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/providers")]
    [ProducesResponseType(typeof(List<OidcProviderInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OidcProviderInfo>>> GetProviders()
    {
        var providers = await _providerService.GetEnabledProvidersAsync();

        var result = providers
            .Select(p => new OidcProviderInfo
            {
                Id = p.Id,
                Name = p.Name,
                Icon = p.Icon,
                ButtonColor = p.ButtonColor,
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Initiate OIDC login flow
    /// Redirects to the OIDC provider's authorization endpoint
    /// </summary>
    /// <param name="provider">Provider ID (optional, uses default if not specified)</param>
    /// <param name="returnUrl">URL to return to after login</param>
    /// <returns>Redirect to OIDC provider</returns>
    [HttpGet("login")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/login")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromQuery] Guid? provider = null,
        [FromQuery] string? returnUrl = null
    )
    {
        if (!_options.Enabled)
        {
            return BadRequest(
                new { error = "oidc_disabled", message = "OIDC authentication is not enabled" }
            );
        }

        // Validate return URL to prevent open redirect attacks
        if (!string.IsNullOrEmpty(returnUrl) && !IsValidReturnUrl(returnUrl))
        {
            return BadRequest(new { error = "invalid_return_url", message = "Invalid return URL" });
        }

        try
        {
            var authRequest = await _authService.GenerateAuthorizationUrlAsync(provider, returnUrl);

            // Store state in a secure cookie for verification on callback
            SetStateCookie(authRequest.State, authRequest.ExpiresAt);

            _logger.LogInformation(
                "Initiating OIDC login for provider {ProviderId}",
                authRequest.ProviderId
            );

            return Redirect(authRequest.AuthorizationUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to generate authorization URL");
            return BadRequest(new { error = "provider_error", message = ex.Message });
        }
    }

    /// <summary>
    /// Handle OIDC callback from provider
    /// Exchanges authorization code for tokens and creates session
    /// </summary>
    /// <param name="code">Authorization code from provider</param>
    /// <param name="state">State parameter for CSRF verification</param>
    /// <param name="error">Error code from provider (if any)</param>
    /// <param name="error_description">Error description from provider</param>
    /// <returns>Redirect to return URL with session cookie set</returns>
    [HttpGet("callback")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description
    )
    {
        // Handle provider errors
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning(
                "OIDC provider returned error: {Error} - {Description}",
                error,
                error_description
            );
            ClearStateCookie();
            return RedirectToError(error, error_description ?? "Authentication failed");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest(
                new { error = "missing_parameters", message = "Code and state are required" }
            );
        }

        // Get expected state from cookie
        var expectedState = Request.Cookies[_options.Cookie.StateCookieName];
        if (string.IsNullOrEmpty(expectedState))
        {
            return RedirectToError(
                "invalid_state",
                "State cookie not found - please try logging in again"
            );
        }

        // Clear state cookie (single use)
        ClearStateCookie();

        // Handle the callback
        var result = await _authService.HandleCallbackAsync(
            code,
            state,
            expectedState,
            GetClientIpAddress(),
            Request.Headers.UserAgent
        );

        if (!result.Success)
        {
            _logger.LogWarning(
                "OIDC callback failed: {Error} - {Description}",
                result.Error,
                result.ErrorDescription
            );
            return RedirectToError(
                result.Error ?? "callback_failed",
                result.ErrorDescription ?? "Authentication failed"
            );
        }

        // Set session cookies
        SetSessionCookies(result.Tokens!);

        _logger.LogInformation(
            "OIDC login successful for user {Name} (subject: {SubjectId})",
            result.UserInfo?.Name,
            result.Tokens?.SubjectId
        );

        // Redirect to return URL
        var returnUrl = result.ReturnUrl ?? _options.DefaultReturnUrl;
        return Redirect(returnUrl);
    }

    /// <summary>
    /// Refresh the session tokens
    /// Uses the refresh token to get a new access token
    /// </summary>
    /// <returns>New token response</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/refresh")]
    [ProducesResponseType(typeof(OidcTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OidcTokenResponse>> Refresh()
    {
        // Get refresh token from cookie or request body
        var refreshToken = GetRefreshToken();
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized(
                new { error = "no_refresh_token", message = "Refresh token not found" }
            );
        }

        var result = await _authService.RefreshSessionAsync(
            refreshToken,
            GetClientIpAddress(),
            Request.Headers.UserAgent
        );

        if (result == null)
        {
            // Clear cookies if refresh failed
            ClearSessionCookies();
            return Unauthorized(
                new
                {
                    error = "invalid_refresh_token",
                    message = "Refresh token is invalid or expired",
                }
            );
        }

        // Update session cookies
        SetSessionCookies(result);

        return Ok(result);
    }

    /// <summary>
    /// Logout and revoke the session
    /// </summary>
    /// <param name="providerId">Provider ID for RP-initiated logout (optional)</param>
    /// <returns>Logout result with optional provider logout URL</returns>
    [HttpPost("logout")]
    [NightscoutEndpoint("/auth/logout")]
    [RemoteCommand]
    [ProducesResponseType(typeof(LogoutResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogoutResponse>> Logout([FromQuery] Guid? providerId = null)
    {
        var refreshToken = GetRefreshToken();

        OidcLogoutResult result;
        if (!string.IsNullOrEmpty(refreshToken))
        {
            result = await _authService.LogoutAsync(refreshToken, providerId);
        }
        else
        {
            result = OidcLogoutResult.Succeeded();
        }

        // Clear session cookies
        ClearSessionCookies();

        _logger.LogInformation("User logged out");

        return Ok(
            new LogoutResponse
            {
                Success = result.Success,
                ProviderLogoutUrl = result.ProviderLogoutUrl,
                Message = "Logged out successfully",
            }
        );
    }

    /// <summary>
    /// Get current user information
    /// </summary>
    /// <returns>User information from the current session</returns>
    [HttpGet("userinfo")]
    [NightscoutEndpoint("/auth/userinfo")]
    [ProducesResponseType(typeof(OidcUserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OidcUserInfo>> GetUserInfo()
    {
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated || !authContext.SubjectId.HasValue)
        {
            return Unauthorized(new { error = "not_authenticated", message = "Not authenticated" });
        }

        var userInfo = await _authService.GetUserInfoAsync(authContext.SubjectId.Value);
        if (userInfo == null)
        {
            return Unauthorized(new { error = "user_not_found", message = "User not found" });
        }

        return Ok(userInfo);
    }

    /// <summary>
    /// Get current session information
    /// </summary>
    /// <returns>Session status</returns>
    [HttpGet("session")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/session")]
    [ProducesResponseType(typeof(SessionInfo), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionInfo>> GetSession()
    {
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated)
        {
            return Ok(new SessionInfo { IsAuthenticated = false });
        }

        var userInfo = authContext.SubjectId.HasValue
            ? await _authService.GetUserInfoAsync(authContext.SubjectId.Value)
            : null;

        return Ok(
            new SessionInfo
            {
                IsAuthenticated = true,
                SubjectId = authContext.SubjectId,
                Name = authContext.SubjectName ?? userInfo?.Name,
                Email = authContext.Email ?? userInfo?.Email,
                Roles = authContext.Roles,
                Permissions = authContext.Permissions,
                ExpiresAt = authContext.ExpiresAt,
                PreferredLanguage = userInfo?.PreferredLanguage,
            }
        );
    }

    #region Private Helper Methods

    /// <summary>
    /// Validate that a return URL is safe (prevents open redirect attacks)
    /// </summary>
    private bool IsValidReturnUrl(string returnUrl)
    {
        // Only allow relative URLs
        if (Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return true;
        }

        // Or URLs that start with our base URL
        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            return returnUrl.StartsWith(_options.BaseUrl, StringComparison.OrdinalIgnoreCase);
        }

        // Or URLs matching allowed patterns
        if (_options.AllowedReturnUrlPatterns.Count > 0)
        {
            return _options.AllowedReturnUrlPatterns.Any(pattern =>
                returnUrl.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
            );
        }

        return false;
    }

    /// <summary>
    /// Set the OIDC state cookie
    /// </summary>
    private void SetStateCookie(string state, DateTimeOffset expiresAt)
    {
        Response.Cookies.Append(
            _options.Cookie.StateCookieName,
            state,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _options.Cookie.Secure,
                SameSite = MapSameSiteMode(_options.Cookie.SameSite),
                Path = _options.Cookie.Path,
                Domain = _options.Cookie.Domain,
                Expires = expiresAt,
            }
        );
    }

    /// <summary>
    /// Clear the OIDC state cookie
    /// </summary>
    private void ClearStateCookie()
    {
        Response.Cookies.Delete(
            _options.Cookie.StateCookieName,
            new CookieOptions { Path = _options.Cookie.Path, Domain = _options.Cookie.Domain }
        );
    }

    /// <summary>
    /// Set session cookies (access token and refresh token)
    /// </summary>
    private void SetSessionCookies(OidcTokenResponse tokens)
    {
        // Access token cookie (short-lived)
        Response.Cookies.Append(
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
        Response.Cookies.Append(
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

        // Also set a non-HttpOnly cookie with just auth status for JavaScript
        Response.Cookies.Append(
            "IsAuthenticated",
            "true",
            new CookieOptions
            {
                HttpOnly = false,
                Secure = _options.Cookie.Secure,
                SameSite = MapSameSiteMode(_options.Cookie.SameSite),
                Path = _options.Cookie.Path,
                Domain = _options.Cookie.Domain,
                Expires = DateTimeOffset.UtcNow.Add(_options.Session.RefreshTokenLifetime),
            }
        );
    }

    /// <summary>
    /// Clear session cookies
    /// </summary>
    private void ClearSessionCookies()
    {
        var cookieOptions = new CookieOptions
        {
            Path = _options.Cookie.Path,
            Domain = _options.Cookie.Domain,
        };

        Response.Cookies.Delete(_options.Cookie.AccessTokenName, cookieOptions);
        Response.Cookies.Delete(_options.Cookie.RefreshTokenName, cookieOptions);
        Response.Cookies.Delete("IsAuthenticated", cookieOptions);
    }

    /// <summary>
    /// Get the refresh token from cookie or request body
    /// </summary>
    private string? GetRefreshToken()
    {
        // First try from cookie
        var refreshToken = Request.Cookies[_options.Cookie.RefreshTokenName];
        if (!string.IsNullOrEmpty(refreshToken))
        {
            return refreshToken;
        }

        // Then try from Authorization header (for API clients)
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (
            !string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Refresh ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return authHeader["Refresh ".Length..].Trim();
        }

        return null;
    }

    /// <summary>
    /// Get the client IP address
    /// </summary>
    private string? GetClientIpAddress()
    {
        // Check for forwarded headers first (when behind a reverse proxy)
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            return forwarded.Split(',').First().Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Redirect to an error page
    /// </summary>
    private IActionResult RedirectToError(string error, string description)
    {
        var returnUrl =
            $"/auth/error?error={Uri.EscapeDataString(error)}&description={Uri.EscapeDataString(description)}";
        return Redirect(returnUrl);
    }

    /// <summary>
    /// Map our SameSite mode to ASP.NET Core's
    /// </summary>
    private static Microsoft.AspNetCore.Http.SameSiteMode MapSameSiteMode(SameSiteMode mode)
    {
        return mode switch
        {
            SameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
            SameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            SameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        };
    }

    #endregion
}

#region Response Models

/// <summary>
/// OIDC provider info for login page
/// </summary>
public class OidcProviderInfo
{
    /// <summary>
    /// Provider ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Icon URL or CSS class
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Button color for UI
    /// </summary>
    public string? ButtonColor { get; set; }
}

/// <summary>
/// Logout response
/// </summary>
public class LogoutResponse
{
    /// <summary>
    /// Whether logout was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// URL for provider logout (if RP-initiated logout is supported)
    /// </summary>
    public string? ProviderLogoutUrl { get; set; }

    /// <summary>
    /// Message
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Current session information
/// </summary>
public class SessionInfo
{
    /// <summary>
    /// Whether the user is authenticated
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Subject ID
    /// </summary>
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// User name
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Email address
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Assigned roles
    /// </summary>
    public List<string>? Roles { get; set; }

    /// <summary>
    /// Resolved permissions
    /// </summary>
    public List<string>? Permissions { get; set; }

    /// <summary>
    /// Session expiration time
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }
}

#endregion
