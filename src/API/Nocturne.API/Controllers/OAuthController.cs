using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers;

/// <summary>
/// OAuth 2.0 endpoints for Nocturne.
/// Supports Authorization Code + PKCE and Device Authorization Grant (RFC 8628).
/// All clients are public (no client secrets); PKCE is mandatory.
/// </summary>
[ApiController]
[Route("oauth")]
[Tags("OAuth")]
public class OAuthController : ControllerBase
{
    private readonly ILogger<OAuthController> _logger;

    /// <summary>
    /// Creates a new instance of OAuthController
    /// </summary>
    public OAuthController(ILogger<OAuthController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Authorization endpoint (Authorization Code + PKCE flow).
    /// Redirects to login if not authenticated, then shows consent screen.
    /// </summary>
    /// <param name="client_id">Client identifier</param>
    /// <param name="redirect_uri">Redirect URI for the authorization code</param>
    /// <param name="response_type">Must be "code"</param>
    /// <param name="scope">Space-delimited requested scopes</param>
    /// <param name="state">Opaque state for CSRF protection</param>
    /// <param name="code_challenge">PKCE code challenge</param>
    /// <param name="code_challenge_method">Must be "S256"</param>
    [HttpGet("authorize")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult Authorize(
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string response_type,
        [FromQuery] string scope,
        [FromQuery] string? state = null,
        [FromQuery] string? code_challenge = null,
        [FromQuery] string? code_challenge_method = null
    )
    {
        // Validate response_type
        if (response_type != "code")
        {
            return BadRequest(new OAuthError
            {
                Error = "unsupported_response_type",
                ErrorDescription = "Only 'code' response type is supported.",
            });
        }

        // Validate PKCE is present (mandatory)
        if (string.IsNullOrEmpty(code_challenge) || code_challenge_method != "S256")
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "PKCE is required. Provide code_challenge with code_challenge_method=S256.",
            });
        }

        // Validate scopes
        var requestedScopes = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var invalidScopes = requestedScopes.Where(s => !OAuthScopes.IsValid(s)).ToList();
        if (invalidScopes.Count > 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = $"Invalid scope(s): {string.Join(", ", invalidScopes)}",
            });
        }

        // Phase 2 will implement:
        // 1. Check if user is authenticated (redirect to login if not)
        // 2. Check if an active grant exists for this client+user with sufficient scopes
        // 3. If grant exists, issue code immediately
        // 4. If not, show consent screen
        // For now, return not implemented
        _logger.LogInformation(
            "OAuth authorize request: client_id={ClientId}, scopes={Scopes}",
            client_id,
            scope
        );

        return StatusCode(StatusCodes.Status501NotImplemented, new OAuthError
        {
            Error = "server_error",
            ErrorDescription = "Authorization code flow will be implemented in Phase 2.",
        });
    }

    /// <summary>
    /// Token endpoint. Handles authorization code exchange, refresh token rotation,
    /// and device code polling.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(OAuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public ActionResult<OAuthTokenResponse> Token([FromForm] OAuthTokenRequest request)
    {
        _logger.LogInformation(
            "OAuth token request: grant_type={GrantType}, client_id={ClientId}",
            request.GrantType,
            request.ClientId
        );

        return request.GrantType switch
        {
            "authorization_code" => StatusCode(StatusCodes.Status501NotImplemented, new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Authorization code exchange will be implemented in Phase 2.",
            }),
            "refresh_token" => StatusCode(StatusCodes.Status501NotImplemented, new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Refresh token rotation will be implemented in Phase 2.",
            }),
            "urn:ietf:params:oauth:grant-type:device_code" => StatusCode(StatusCodes.Status501NotImplemented, new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Device code exchange will be implemented in Phase 3.",
            }),
            _ => BadRequest(new OAuthError
            {
                Error = "unsupported_grant_type",
                ErrorDescription = $"Unsupported grant_type: {request.GrantType}",
            }),
        };
    }

    /// <summary>
    /// Device Authorization endpoint (RFC 8628).
    /// Used by headless clients (CLI tools, scripts, IoT devices, pump rigs).
    /// </summary>
    [HttpPost("device")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(OAuthDeviceAuthorizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public ActionResult<OAuthDeviceAuthorizationResponse> DeviceAuthorization(
        [FromForm] string client_id,
        [FromForm] string? scope = null
    )
    {
        // Validate scopes
        var requestedScopes = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var invalidScopes = requestedScopes.Where(s => !OAuthScopes.IsValid(s)).ToList();
        if (invalidScopes.Count > 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = $"Invalid scope(s): {string.Join(", ", invalidScopes)}",
            });
        }

        _logger.LogInformation(
            "OAuth device authorization request: client_id={ClientId}, scopes={Scopes}",
            client_id,
            scope
        );

        // Phase 3 will implement the full device flow
        return StatusCode(StatusCodes.Status501NotImplemented, new OAuthError
        {
            Error = "server_error",
            ErrorDescription = "Device authorization flow will be implemented in Phase 3.",
        });
    }

    /// <summary>
    /// Token revocation endpoint (RFC 7009).
    /// Revokes an access or refresh token.
    /// </summary>
    [HttpPost("revoke")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public ActionResult Revoke(
        [FromForm] string token,
        [FromForm] string? token_type_hint = null
    )
    {
        _logger.LogInformation(
            "OAuth revoke request: token_type_hint={TokenTypeHint}",
            token_type_hint
        );

        // Phase 2 will implement token revocation
        // Per RFC 7009, the server MUST respond with HTTP 200 even if the token is invalid
        return Ok();
    }
}

#region Request/Response Models

/// <summary>
/// OAuth 2.0 error response (RFC 6749 Section 5.2)
/// </summary>
public class OAuthError
{
    public string Error { get; set; } = string.Empty;
    public string? ErrorDescription { get; set; }
    public string? ErrorUri { get; set; }
}

/// <summary>
/// OAuth 2.0 token request (supports multiple grant types via form post)
/// </summary>
public class OAuthTokenRequest
{
    [FromForm(Name = "grant_type")]
    public string GrantType { get; set; } = string.Empty;

    [FromForm(Name = "client_id")]
    public string? ClientId { get; set; }

    [FromForm(Name = "code")]
    public string? Code { get; set; }

    [FromForm(Name = "redirect_uri")]
    public string? RedirectUri { get; set; }

    [FromForm(Name = "code_verifier")]
    public string? CodeVerifier { get; set; }

    [FromForm(Name = "refresh_token")]
    public string? RefreshToken { get; set; }

    [FromForm(Name = "device_code")]
    public string? DeviceCode { get; set; }

    [FromForm(Name = "scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// OAuth 2.0 token response (RFC 6749 Section 5.1)
/// </summary>
public class OAuthTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public int ExpiresIn { get; set; }
    public string? RefreshToken { get; set; }
    public string? Scope { get; set; }
}

/// <summary>
/// Device Authorization Response (RFC 8628 Section 3.2)
/// </summary>
public class OAuthDeviceAuthorizationResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public string? VerificationUriComplete { get; set; }
    public int ExpiresIn { get; set; }
    public int Interval { get; set; } = 5;
}

#endregion
