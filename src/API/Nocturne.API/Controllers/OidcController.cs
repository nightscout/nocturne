using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for OIDC authentication flows
/// Handles login initiation, OAuth callback, logout, and session management
/// </summary>
[ApiController]
[Route("api/v4/oidc")]
[Tags("Oidc")]
public class OidcController : ControllerBase
{
    private readonly IOidcAuthService _authService;
    private readonly IOidcProviderService _providerService;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly OidcOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Creates a new instance of OidcController
    /// </summary>
    public OidcController(
        IOidcAuthService authService,
        IOidcProviderService providerService,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        IOptions<OidcOptions> options,
        IConfiguration configuration,
        ILogger<OidcController> logger
    )
    {
        _authService = authService;
        _providerService = providerService;
        _subjectService = subjectService;
        _auditService = auditService;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get available OIDC providers for login
    /// </summary>
    /// <returns>List of enabled providers</returns>
    [HttpGet("providers")]
    [AllowAnonymous]
    [AllowDuringSetup]
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
    [AllowDuringSetup]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromQuery] Guid? provider = null,
        [FromQuery] string? returnUrl = null
    )
    {
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
    [AllowDuringSetup]
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

            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subjectId: null, success: false,
                ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent,
                errorMessage: result.ErrorDescription,
                detailsJson: JsonSerializer.Serialize(new { method = "oidc" }));

            return RedirectToError(
                result.Error ?? "callback_failed",
                result.ErrorDescription ?? "Authentication failed"
            );
        }

        // Set session cookies
        SetSessionCookies(result.Tokens!);

        await _auditService.LogAsync(AuthAuditEventType.Login, result.Tokens?.SubjectId, success: true,
            ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent,
            detailsJson: JsonSerializer.Serialize(new { method = "oidc" }));

        _logger.LogInformation(
            "OIDC login successful for user {Name} (subject: {SubjectId})",
            result.UserInfo?.Name,
            result.Tokens?.SubjectId
        );

        // Redirect to return URL
        var returnUrl = result.ReturnUrl ?? "/";
        return Redirect(returnUrl);
    }

    /// <summary>
    /// Initiate the OIDC link flow.
    /// Redirects an already-authenticated caller to the OIDC provider's authorization
    /// endpoint so they can attach the external identity to their current account.
    /// </summary>
    [HttpGet("link")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Link(
        [FromQuery] Guid provider,
        [FromQuery] string? returnUrl = null)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
            return Unauthorized(new { error = "not_authenticated", message = "Authentication required" });

        if (!string.IsNullOrEmpty(returnUrl) && !IsValidReturnUrl(returnUrl))
            return BadRequest(new { error = "invalid_return_url", message = "Invalid return URL" });

        try
        {
            var req = await _authService.GenerateLinkAuthorizationUrlAsync(
                provider, auth.SubjectId.Value, returnUrl);
            SetLinkStateCookie(req.State, req.ExpiresAt);

            _logger.LogInformation(
                "Initiating OIDC link flow for subject {SubjectId} via provider {ProviderId}",
                auth.SubjectId, req.ProviderId);

            return Redirect(req.AuthorizationUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to generate link authorization URL");
            return BadRequest(new { error = "provider_error", message = ex.Message });
        }
    }

    /// <summary>
    /// Handle the OIDC link callback. Verifies the authorization code against the IdP,
    /// then attaches the external identity to the currently-authenticated subject.
    /// Does NOT issue new session cookies.
    /// </summary>
    [HttpGet("link/callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
        {
            // Session expired mid-flow: the link-state cookie is useless without a session, so clear it.
            ClearLinkStateCookie();
            return Redirect("/auth/login?returnUrl=/settings/account");
        }

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("OIDC provider returned error on link callback: {Error} - {Description}",
                error, error_description);
            ClearLinkStateCookie();
            return RedirectToError(error, error_description ?? "Link failed");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            ClearLinkStateCookie();
            return BadRequest(new { error = "missing_parameters", message = "Code and state are required" });
        }

        var expectedState = Request.Cookies[_options.Cookie.LinkStateCookieName];
        if (string.IsNullOrEmpty(expectedState))
            return RedirectToError("invalid_state", "Link state cookie not found — please try linking again");

        ClearLinkStateCookie();

        var result = await _authService.HandleLinkCallbackAsync(
            code, state, expectedState,
            auth.SubjectId.Value,
            GetClientIpAddress(),
            Request.Headers.UserAgent);

        if (!result.Success)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, auth.SubjectId, success: false,
                ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent,
                errorMessage: result.ErrorDescription,
                detailsJson: JsonSerializer.Serialize(new { method = "oidc_link" }));

            return RedirectToError(result.Error ?? "link_failed", result.ErrorDescription ?? "Link failed");
        }

        await _auditService.LogAsync(AuthAuditEventType.OidcIdentityLinked, auth.SubjectId, success: true,
            ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent,
            detailsJson: JsonSerializer.Serialize(new { identityId = result.IdentityId }));

        _logger.LogInformation(
            "OIDC identity {IdentityId} linked to subject {SubjectId}",
            result.IdentityId, auth.SubjectId);

        var returnUrl = result.ReturnUrl ?? "/settings/account";
        var separator = returnUrl.Contains('?') ? '&' : '?';
        return Redirect($"{returnUrl}{separator}linked=success");
    }

    /// <summary>
    /// List OIDC identities linked to the currently-authenticated subject.
    /// </summary>
    [HttpGet("link/identities")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedOidcIdentitiesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LinkedOidcIdentitiesResponse>> GetLinkedIdentities()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
            return Unauthorized(new { error = "not_authenticated" });

        var list = await _subjectService.GetLinkedOidcIdentitiesAsync(auth.SubjectId.Value);

        return Ok(new LinkedOidcIdentitiesResponse
        {
            Identities = list.Select(i => new LinkedOidcIdentityDto
            {
                Id = i.Id,
                ProviderId = i.ProviderId,
                ProviderName = i.ProviderName,
                ProviderIcon = i.ProviderIcon,
                ProviderButtonColor = i.ProviderButtonColor,
                Email = i.Email,
                LinkedAt = i.LinkedAt,
                LastUsedAt = i.LastUsedAt,
            }).ToList(),
        });
    }

    /// <summary>
    /// Unlink an OIDC identity from the currently-authenticated subject.
    /// Blocked if this would leave the subject with zero primary auth factors.
    /// </summary>
    [HttpDelete("link/identities/{identityId:guid}")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnlinkIdentity(Guid identityId)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
            return Unauthorized();

        // Symmetric factor-count rule is enforced atomically inside the service inside a
        // serializable transaction to prevent TOCTOU races between concurrent removals.
        var result = await _subjectService.TryRemoveOidcIdentityAsync(auth.SubjectId.Value, identityId);
        switch (result)
        {
            case FactorRemovalResult.NotFound:
                return NotFound();
            case FactorRemovalResult.LastPrimaryFactor:
                return Conflict(new
                {
                    error = "last_factor",
                    message = "Cannot remove your only remaining sign-in method",
                });
            case FactorRemovalResult.Removed:
                await _auditService.LogAsync(AuthAuditEventType.OidcIdentityUnlinked, auth.SubjectId, success: true,
                    ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent,
                    detailsJson: JsonSerializer.Serialize(new { identityId }));

                _logger.LogInformation(
                    "OIDC identity {IdentityId} unlinked from subject {SubjectId}",
                    identityId, auth.SubjectId);

                return NoContent();
            default:
                throw new InvalidOperationException($"Unexpected FactorRemovalResult: {result}");
        }
    }

    /// <summary>
    /// Refresh the session tokens
    /// Uses the refresh token to get a new access token
    /// </summary>
    /// <returns>New token response</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
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

        await _auditService.LogAsync(AuthAuditEventType.TokenRefreshed, result.SubjectId, success: true,
            ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent);

        return Ok(result);
    }

    /// <summary>
    /// Logout and revoke the session
    /// </summary>
    /// <param name="providerId">Provider ID for RP-initiated logout (optional)</param>
    /// <returns>Logout result with optional provider logout URL</returns>
    [HttpPost("logout")]
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

        var authContext = HttpContext.GetAuthContext();
        await _auditService.LogAsync(AuthAuditEventType.Logout, authContext?.SubjectId, success: true,
            ipAddress: GetClientIpAddress(), userAgent: Request.Headers.UserAgent);

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
                IsPlatformAdmin = authContext.IsPlatformAdmin,
            }
        );
    }

    #region Private Helper Methods

    /// <summary>
    /// Validate that a return URL is safe (prevents open redirect attacks)
    /// </summary>
    private bool IsValidReturnUrl(string returnUrl)
    {
        if (Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return true;
        }

        var baseUrl = _configuration[ServiceNames.ConfigKeys.BaseUrl];
        if (!string.IsNullOrEmpty(baseUrl))
        {
            return returnUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase);
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
    /// Set the OIDC link state cookie
    /// </summary>
    private void SetLinkStateCookie(string state, DateTimeOffset expiresAt)
    {
        Response.Cookies.Append(
            _options.Cookie.LinkStateCookieName,
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
    /// Clear the OIDC link state cookie
    /// </summary>
    private void ClearLinkStateCookie()
    {
        Response.Cookies.Delete(
            _options.Cookie.LinkStateCookieName,
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

    /// <summary>
    /// Whether this subject has platform-level admin access
    /// </summary>
    public bool IsPlatformAdmin { get; set; }
}

/// <summary>
/// Response containing linked OIDC identities for the current subject
/// </summary>
public class LinkedOidcIdentitiesResponse
{
    public List<LinkedOidcIdentityDto> Identities { get; set; } = new();
}

/// <summary>
/// DTO describing a single OIDC identity linked to a subject
/// </summary>
public class LinkedOidcIdentityDto
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderIcon { get; set; }
    public string? ProviderButtonColor { get; set; }
    public string? Email { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

#endregion
