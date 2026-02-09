using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers;

/// <summary>
/// OAuth 2.0 endpoints for Nocturne.
/// Supports Authorization Code + PKCE and Device Authorization Grant (RFC 8628).
/// All clients are public (no client secrets); PKCE is mandatory.
/// </summary>
[ApiController]
[Route("api/oauth")]
[Tags("OAuth")]
public class OAuthController : ControllerBase
{
    private readonly IOAuthClientService _clientService;
    private readonly IOAuthGrantService _grantService;
    private readonly IOAuthTokenService _tokenService;
    private readonly IOAuthDeviceCodeService _deviceCodeService;
    private readonly IFollowerInviteService _inviteService;
    private readonly ISubjectService _subjectService;
    private readonly IJwtService _jwtService;
    private readonly IOAuthTokenRevocationCache _revocationCache;
    private readonly ILocalIdentityService _localIdentityService;
    private readonly ILogger<OAuthController> _logger;

    /// <summary>
    /// Creates a new instance of OAuthController
    /// </summary>
    public OAuthController(
        IOAuthClientService clientService,
        IOAuthGrantService grantService,
        IOAuthTokenService tokenService,
        IOAuthDeviceCodeService deviceCodeService,
        IFollowerInviteService inviteService,
        ISubjectService subjectService,
        IJwtService jwtService,
        IOAuthTokenRevocationCache revocationCache,
        ILocalIdentityService localIdentityService,
        ILogger<OAuthController> logger
    )
    {
        _clientService = clientService;
        _grantService = grantService;
        _tokenService = tokenService;
        _deviceCodeService = deviceCodeService;
        _inviteService = inviteService;
        _subjectService = subjectService;
        _jwtService = jwtService;
        _revocationCache = revocationCache;
        _localIdentityService = localIdentityService;
        _logger = logger;
    }

    /// <summary>
    /// Authorization endpoint (Authorization Code + PKCE flow).
    /// Redirects to login if not authenticated, then shows consent screen.
    /// </summary>
    [HttpGet("authorize")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Authorize(
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

        if (requestedScopes.Length == 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = "At least one scope must be requested.",
            });
        }

        // Find or create the client
        var client = await _clientService.FindOrCreateClientAsync(client_id);

        // Validate redirect URI
        if (!await _clientService.ValidateRedirectUriAsync(client_id, redirect_uri))
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Invalid redirect_uri for this client.",
            });
        }

        // Check if user is authenticated
        if (!HttpContext.IsAuthenticated())
        {
            // Redirect to login, preserving the OAuth params to return to after login
            var returnUrl = $"/api/oauth/authorize{Request.QueryString}";
            return Redirect($"/auth/local/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        // Normalize the requested scopes
        var normalizedScopes = OAuthScopes.Normalize(requestedScopes);

        // Check if an active grant exists with sufficient scopes
        var existingGrant = await _grantService.GetActiveGrantAsync(client.Id, subjectId.Value);
        if (existingGrant != null)
        {
            var existingSet = new HashSet<string>(existingGrant.Scopes);
            var allSatisfied = normalizedScopes.All(s => OAuthScopes.SatisfiesScope(existingSet, s));

            if (allSatisfied)
            {
                // Silent approval: existing grant covers all requested scopes
                return await IssueAuthorizationCode(
                    client.Id,
                    subjectId.Value,
                    normalizedScopes,
                    redirect_uri,
                    code_challenge,
                    state
                );
            }
        }

        // Redirect to consent page, passing existing scopes for the upgrade UI
        var existingScopeString = existingGrant != null
            ? string.Join(" ", existingGrant.Scopes)
            : null;
        var consentUrl = BuildConsentUrl(client_id, redirect_uri, scope, state, code_challenge, existingScopeString);
        return Redirect(consentUrl);
    }

    /// <summary>
    /// Consent approval endpoint. Called by the consent page when the user approves.
    /// </summary>
    [HttpPost("authorize")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ApproveConsent([FromForm] ConsentApprovalRequest request)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        // If user denied
        if (!request.Approved)
        {
            return RedirectWithError(
                request.RedirectUri,
                "access_denied",
                "The user denied the authorization request.",
                request.State
            );
        }

        // Validate scopes
        var scopes = request.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var normalizedScopes = OAuthScopes.Normalize(scopes);

        if (normalizedScopes.Count == 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = "No valid scopes were approved.",
            });
        }

        // Find the client
        var client = await _clientService.FindOrCreateClientAsync(request.ClientId);

        // Re-validate redirect URI to prevent manipulation between GET and POST
        if (!await _clientService.ValidateRedirectUriAsync(request.ClientId, request.RedirectUri))
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Invalid redirect_uri for this client.",
            });
        }

        // Generate authorization code
        return await IssueAuthorizationCode(
            client.Id,
            subjectId.Value,
            normalizedScopes,
            request.RedirectUri,
            request.CodeChallenge,
            request.State,
            request.LimitTo24Hours
        );
    }

    /// <summary>
    /// Token endpoint. Handles authorization code exchange, refresh token rotation,
    /// and device code polling.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting("oauth-token")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(OAuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Token([FromForm] OAuthTokenRequest request)
    {
        _logger.LogInformation(
            "OAuth token request: grant_type={GrantType}, client_id={ClientId}",
            request.GrantType,
            request.ClientId
        );

        OAuthTokenResult result;

        switch (request.GrantType)
        {
            case "authorization_code":
                if (string.IsNullOrEmpty(request.Code) ||
                    string.IsNullOrEmpty(request.CodeVerifier) ||
                    string.IsNullOrEmpty(request.RedirectUri) ||
                    string.IsNullOrEmpty(request.ClientId))
                {
                    return BadRequest(new OAuthError
                    {
                        Error = "invalid_request",
                        ErrorDescription = "Missing required parameters: code, code_verifier, redirect_uri, client_id.",
                    });
                }

                result = await _tokenService.ExchangeAuthorizationCodeAsync(
                    request.Code,
                    request.CodeVerifier,
                    request.RedirectUri,
                    request.ClientId
                );
                break;

            case "refresh_token":
                if (string.IsNullOrEmpty(request.RefreshToken))
                {
                    return BadRequest(new OAuthError
                    {
                        Error = "invalid_request",
                        ErrorDescription = "Missing required parameter: refresh_token.",
                    });
                }

                result = await _tokenService.RefreshAccessTokenAsync(
                    request.RefreshToken,
                    request.ClientId
                );
                break;

            case "urn:ietf:params:oauth:grant-type:device_code":
                if (string.IsNullOrEmpty(request.DeviceCode) ||
                    string.IsNullOrEmpty(request.ClientId))
                {
                    return BadRequest(new OAuthError
                    {
                        Error = "invalid_request",
                        ErrorDescription = "Missing required parameters: device_code, client_id.",
                    });
                }

                result = await _tokenService.ExchangeDeviceCodeAsync(
                    request.DeviceCode,
                    request.ClientId
                );
                break;

            default:
                return BadRequest(new OAuthError
                {
                    Error = "unsupported_grant_type",
                    ErrorDescription = $"Unsupported grant_type: {request.GrantType}",
                });
        }

        if (!result.Success)
        {
            return BadRequest(new OAuthError
            {
                Error = result.Error!,
                ErrorDescription = result.ErrorDescription,
            });
        }

        return Ok(new OAuthTokenResponse
        {
            AccessToken = result.AccessToken!,
            TokenType = "Bearer",
            ExpiresIn = result.ExpiresIn,
            RefreshToken = result.RefreshToken,
            Scope = result.Scope,
        });
    }

    /// <summary>
    /// Device Authorization endpoint (RFC 8628).
    /// Used by headless clients (CLI tools, scripts, IoT devices, pump rigs).
    /// </summary>
    [HttpPost("device")]
    [AllowAnonymous]
    [EnableRateLimiting("oauth-device")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(OAuthDeviceAuthorizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OAuthDeviceAuthorizationResponse>> DeviceAuthorization(
        [FromForm] string client_id,
        [FromForm] string? scope = null
    )
    {
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

        if (requestedScopes.Length == 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = "At least one scope must be requested.",
            });
        }

        // Validate client
        await _clientService.FindOrCreateClientAsync(client_id);

        // Normalize scopes
        var normalizedScopes = OAuthScopes.Normalize(requestedScopes);

        // Create device code pair
        var result = await _deviceCodeService.CreateDeviceCodeAsync(client_id, normalizedScopes);

        // Build verification URI from current request
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var verificationUri = $"{baseUrl}/api/oauth/device";
        var verificationUriComplete = $"{verificationUri}?user_code={Uri.EscapeDataString(result.UserCode)}";

        _logger.LogInformation(
            "Device authorization initiated for client {ClientId}, user_code={UserCode}",
            client_id,
            result.UserCode
        );

        return Ok(new OAuthDeviceAuthorizationResponse
        {
            DeviceCode = result.DeviceCode,
            UserCode = result.UserCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = verificationUriComplete,
            ExpiresIn = result.ExpiresIn,
            Interval = result.Interval,
        });
    }

    /// <summary>
    /// Get device code info for the approval page.
    /// </summary>
    [HttpGet("device-info")]
    [ProducesResponseType(typeof(DeviceCodeInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceCodeInfo>> GetDeviceInfo(
        [FromQuery] string user_code
    )
    {
        if (string.IsNullOrEmpty(user_code))
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Missing required parameter: user_code.",
            });
        }

        var info = await _deviceCodeService.GetByUserCodeAsync(user_code);
        if (info == null)
        {
            return NotFound(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Device code not found or has expired.",
            });
        }

        return Ok(info);
    }

    /// <summary>
    /// Approve or deny a device authorization request.
    /// Called by the device approval page.
    /// </summary>
    [HttpPost("device-approve")]
    [EnableRateLimiting("oauth-device-approve")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeviceApprove(
        [FromForm] DeviceApprovalRequest request
    )
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        if (string.IsNullOrEmpty(request.UserCode))
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Missing required parameter: user_code.",
            });
        }

        var success = request.Approved
            ? await _deviceCodeService.ApproveDeviceCodeAsync(request.UserCode, subjectId.Value)
            : await _deviceCodeService.DenyDeviceCodeAsync(request.UserCode);

        if (!success)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Device code is invalid, expired, or already processed.",
            });
        }

        return Ok(new { approved = request.Approved });
    }

    /// <summary>
    /// Token revocation endpoint (RFC 7009).
    /// </summary>
    [HttpPost("revoke")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Revoke(
        [FromForm] string token,
        [FromForm] string? token_type_hint = null
    )
    {
        await _tokenService.RevokeTokenAsync(token, token_type_hint);
        return Ok();
    }

    /// <summary>
    /// Get client info for the consent page.
    /// </summary>
    [HttpGet("client-info")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OAuthClientInfoResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OAuthClientInfoResponse>> GetClientInfo(
        [FromQuery] string client_id
    )
    {
        var client = await _clientService.FindOrCreateClientAsync(client_id);
        var knownEntry = KnownOAuthClients.Match(client_id);

        return Ok(new OAuthClientInfoResponse
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName,
            IsKnown = client.IsKnown,
            Homepage = knownEntry?.Homepage,
        });
    }

    private async Task<ActionResult> IssueAuthorizationCode(
        Guid clientEntityId,
        Guid subjectId,
        IReadOnlySet<string> scopes,
        string redirectUri,
        string codeChallenge,
        string? state,
        bool limitTo24Hours = false
    )
    {
        var code = await _tokenService.GenerateAuthorizationCodeAsync(
            clientEntityId,
            subjectId,
            scopes,
            redirectUri,
            codeChallenge,
            limitTo24Hours
        );

        var separator = redirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}";

        if (!string.IsNullOrEmpty(state))
        {
            redirectUrl += $"&state={Uri.EscapeDataString(state)}";
        }

        return Redirect(redirectUrl);
    }

    private ActionResult RedirectWithError(
        string redirectUri,
        string error,
        string errorDescription,
        string? state
    )
    {
        var separator = redirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{redirectUri}{separator}error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(errorDescription)}";

        if (!string.IsNullOrEmpty(state))
        {
            redirectUrl += $"&state={Uri.EscapeDataString(state)}";
        }

        return Redirect(redirectUrl);
    }

    private static string BuildConsentUrl(
        string clientId,
        string redirectUri,
        string scope,
        string? state,
        string codeChallenge,
        string? existingScopes = null
    )
    {
        var qs = $"client_id={Uri.EscapeDataString(clientId)}" +
                 $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                 $"&scope={Uri.EscapeDataString(scope)}" +
                 $"&code_challenge={Uri.EscapeDataString(codeChallenge)}";

        if (!string.IsNullOrEmpty(state))
        {
            qs += $"&state={Uri.EscapeDataString(state)}";
        }

        if (!string.IsNullOrEmpty(existingScopes))
        {
            qs += $"&existing_scopes={Uri.EscapeDataString(existingScopes)}";
        }

        return $"/api/oauth/consent?{qs}";
    }

    /// <summary>
    /// List all active grants for the authenticated user.
    /// </summary>
    [HttpGet("grants")]
    [ProducesResponseType(typeof(OAuthGrantListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OAuthGrantListResponse>> GetGrants()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        var grants = await _grantService.GetGrantsForSubjectAsync(subjectId.Value);
        var dtos = grants.Select(MapToDto).ToList();

        return Ok(new OAuthGrantListResponse { Grants = dtos });
    }

    /// <summary>
    /// Revoke (delete) a specific grant owned by the authenticated user.
    /// </summary>
    [HttpDelete("grants/{grantId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteGrant(Guid grantId)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        // Verify ownership: load all grants for the subject and check if grantId is among them
        var grants = await _grantService.GetGrantsForSubjectAsync(subjectId.Value);
        if (grants.All(g => g.Id != grantId))
        {
            return NotFound(new OAuthError
            {
                Error = "not_found",
                ErrorDescription = "Grant not found.",
            });
        }

        await _grantService.RevokeGrantAsync(grantId);
        return NoContent();
    }

    /// <summary>
    /// Create a follower grant (share data with another user by email).
    /// </summary>
    [HttpPost("grants/follower")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(OAuthGrantDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OAuthGrantDto>> CreateFollowerGrant(
        [FromBody] CreateFollowerGrantRequest request
    )
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.FollowerEmail))
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Follower email is required.",
            });
        }

        if (request.Scopes == null || request.Scopes.Count == 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "At least one scope is required.",
            });
        }

        // Look up follower subject by email
        var subjects = await _subjectService.GetSubjectsAsync(new SubjectFilter
        {
            EmailContains = request.FollowerEmail,
            Limit = 100,
        });
        var follower = subjects.FirstOrDefault(s =>
            string.Equals(s.Email, request.FollowerEmail, StringComparison.OrdinalIgnoreCase));

        // If follower doesn't exist and a temporary password is provided, create them
        if (follower == null && !string.IsNullOrWhiteSpace(request.TemporaryPassword))
        {
            try
            {
                var registrationResult = await _localIdentityService.RegisterAsync(
                    email: request.FollowerEmail,
                    password: request.TemporaryPassword,
                    displayName: request.FollowerDisplayName ?? request.FollowerEmail,
                    skipAllowlistCheck: true,
                    autoVerifyEmail: true
                );

                if (!registrationResult.Success)
                {
                    return BadRequest(new OAuthError
                    {
                        Error = "registration_failed",
                        ErrorDescription = registrationResult.ErrorMessage ?? "Failed to create follower account.",
                    });
                }

                // Set temporary password flag
                await _localIdentityService.SetTemporaryPasswordAsync(
                    registrationResult.User!.Id,
                    request.TemporaryPassword,
                    subjectId.Value
                );

                // Assign follower role
                if (registrationResult.SubjectId.HasValue)
                {
                    await _subjectService.AssignRoleAsync(
                        registrationResult.SubjectId.Value,
                        "follower",
                        subjectId.Value
                    );
                }

                // Get the newly created subject
                follower = await _subjectService.GetSubjectByIdAsync(registrationResult.SubjectId!.Value);

                _logger.LogInformation(
                    "Created follower account for {Email} by admin {AdminId}",
                    request.FollowerEmail,
                    subjectId.Value
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create follower account for {Email}", request.FollowerEmail);
                return BadRequest(new OAuthError
                {
                    Error = "creation_failed",
                    ErrorDescription = "Failed to create follower account.",
                });
            }
        }

        if (follower == null)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Follower not found.",
            });
        }

        if (follower.Id == subjectId.Value)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Cannot create a follower grant for yourself.",
            });
        }

        try
        {
            var grant = await _grantService.CreateFollowerGrantAsync(
                subjectId.Value,
                follower.Id,
                request.Scopes,
                request.Label
            );

            return StatusCode(StatusCodes.Status201Created, MapToDto(grant));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = ex.Message,
            });
        }
    }

    /// <summary>
    /// Update a grant's label and/or scopes.
    /// </summary>
    [HttpPatch("grants/{grantId}")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(OAuthGrantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OAuthGrantDto>> UpdateGrant(
        Guid grantId,
        [FromBody] UpdateGrantRequest request
    )
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        try
        {
            var updated = await _grantService.UpdateGrantAsync(
                grantId,
                subjectId.Value,
                request.Label,
                request.Scopes
            );

            if (updated == null)
            {
                return NotFound(new OAuthError
                {
                    Error = "not_found",
                    ErrorDescription = "Grant not found.",
                });
            }

            return Ok(MapToDto(updated));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = ex.Message,
            });
        }
    }

    /// <summary>
    /// List data owners that the authenticated user can view as a follower.
    /// Used by the frontend to populate the "Viewing data for:" selector.
    /// </summary>
    [HttpGet("follower-targets")]
    [ProducesResponseType(typeof(FollowerTargetListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<FollowerTargetListResponse>> GetFollowerTargets()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        var grants = await _grantService.GetGrantsAsFollowerAsync(subjectId.Value);

        var targets = new List<FollowerTargetDto>();
        foreach (var grant in grants)
        {
            // Look up the data owner's info
            var owner = await _subjectService.GetSubjectByIdAsync(grant.SubjectId);
            targets.Add(new FollowerTargetDto
            {
                SubjectId = grant.SubjectId,
                DisplayName = owner?.Name,
                Email = owner?.Email,
                Scopes = grant.Scopes,
                Label = grant.Label,
            });
        }

        return Ok(new FollowerTargetListResponse { Targets = targets });
    }

    // ============================================================================
    // Follower Invite Endpoints
    // ============================================================================

    /// <summary>
    /// Create a follower invite link.
    /// The link can be shared with someone who doesn't have an account yet.
    /// </summary>
    [HttpPost("invites")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CreateInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateInviteResponse>> CreateInvite(
        [FromBody] CreateInviteRequest request)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        if (request.Scopes == null || request.Scopes.Count == 0)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "At least one scope is required.",
            });
        }

        try
        {
            TimeSpan? expiresIn = request.ExpiresInDays.HasValue
                ? TimeSpan.FromDays(request.ExpiresInDays.Value)
                : null;

            var result = await _inviteService.CreateInviteAsync(
                subjectId.Value,
                request.Scopes,
                request.Label,
                expiresIn,
                request.MaxUses,
                request.LimitTo24Hours);

            return StatusCode(StatusCodes.Status201Created, new CreateInviteResponse
            {
                Id = result.Id,
                Token = result.Token,
                InviteUrl = result.InviteUrl,
                ExpiresAt = result.ExpiresAt,
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new OAuthError
            {
                Error = "invalid_scope",
                ErrorDescription = ex.Message,
            });
        }
    }

    /// <summary>
    /// List invites created by the authenticated user.
    /// </summary>
    [HttpGet("invites")]
    [ProducesResponseType(typeof(InviteListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InviteListResponse>> ListInvites()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        var invites = await _inviteService.GetInvitesForOwnerAsync(subjectId.Value);

        return Ok(new InviteListResponse
        {
            Invites = invites.Select(i => new InviteDto
            {
                Id = i.Id,
                Scopes = i.Scopes,
                Label = i.Label,
                ExpiresAt = i.ExpiresAt,
                MaxUses = i.MaxUses,
                UseCount = i.UseCount,
                CreatedAt = i.CreatedAt,
                IsValid = i.IsValid,
                IsExpired = i.IsExpired,
                IsRevoked = i.IsRevoked,
                LimitTo24Hours = i.LimitTo24Hours,
                UsedBy = i.UsedBy.Select(u => new InviteUsageDto
                {
                    FollowerSubjectId = u.FollowerSubjectId,
                    FollowerName = u.FollowerName,
                    FollowerEmail = u.FollowerEmail,
                    UsedAt = u.UsedAt,
                }).ToList(),
            }).ToList(),
        });
    }

    /// <summary>
    /// Revoke an invite so it can no longer be used.
    /// </summary>
    [HttpDelete("invites/{inviteId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvite(Guid inviteId)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "User is not authenticated.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        await _inviteService.RevokeInviteAsync(inviteId, subjectId.Value);
        return NoContent();
    }

    /// <summary>
    /// Get invite details by token (for the accept page).
    /// This is a public endpoint so invitees can see what they're accepting.
    /// </summary>
    [HttpGet("invites/{token}/info")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(InviteInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InviteInfoResponse>> GetInviteInfo(string token)
    {
        var invite = await _inviteService.GetInviteByTokenAsync(token);

        if (invite == null)
        {
            return NotFound(new OAuthError
            {
                Error = "not_found",
                ErrorDescription = "Invite not found or has expired.",
            });
        }

        return Ok(new InviteInfoResponse
        {
            OwnerName = invite.OwnerName,
            OwnerEmail = invite.OwnerEmail,
            Scopes = invite.Scopes,
            Label = invite.Label,
            ExpiresAt = invite.ExpiresAt,
            IsValid = invite.IsValid,
            IsExpired = invite.IsExpired,
            IsRevoked = invite.IsRevoked,
            LimitTo24Hours = invite.LimitTo24Hours,
        });
    }

    /// <summary>
    /// Accept an invite and create the follower grant.
    /// Requires authentication - the invitee must be logged in.
    /// </summary>
    [HttpPost("invites/{token}/accept")]
    [ProducesResponseType(typeof(AcceptInviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AcceptInviteResponse>> AcceptInvite(string token)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "You must be logged in to accept an invite.",
            });
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
        {
            return Unauthorized(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = "Could not determine authenticated user.",
            });
        }

        var result = await _inviteService.AcceptInviteAsync(token, subjectId.Value);

        if (!result.Success)
        {
            return BadRequest(new OAuthError
            {
                Error = result.Error ?? "accept_failed",
                ErrorDescription = result.ErrorDescription ?? "Failed to accept invite.",
            });
        }

        return Ok(new AcceptInviteResponse
        {
            Success = true,
            GrantId = result.GrantId,
        });
    }

    /// <summary>
    /// Token introspection endpoint (RFC 7662).
    /// Returns metadata about a token including its active status, scopes, and subject.
    /// Per RFC 7662, always returns 200 OK; invalid tokens get active=false.
    /// </summary>
    [HttpPost("introspect")]
    [AllowAnonymous]
    [EnableRateLimiting("oauth-token")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(TokenIntrospectionResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenIntrospectionResponse>> Introspect(
        [FromForm] string token,
        [FromForm] string? token_type_hint = null)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Ok(new TokenIntrospectionResponse { Active = false });
        }

        // Try as JWT access token
        if (token.Contains('.'))
        {
            var validation = _jwtService.ValidateAccessToken(token);
            if (validation.IsValid && validation.Claims != null)
            {
                var claims = validation.Claims;

                // Check revocation cache
                if (!string.IsNullOrEmpty(claims.JwtId) &&
                    await _revocationCache.IsRevokedAsync(claims.JwtId))
                {
                    return Ok(new TokenIntrospectionResponse { Active = false });
                }

                return Ok(new TokenIntrospectionResponse
                {
                    Active = true,
                    Scope = claims.Scopes.Count > 0 ? string.Join(" ", claims.Scopes) : null,
                    ClientId = claims.ClientId,
                    Sub = claims.SubjectId.ToString(),
                    Exp = claims.ExpiresAt.ToUnixTimeSeconds(),
                    Iat = claims.IssuedAt.ToUnixTimeSeconds(),
                    Jti = claims.JwtId,
                    TokenType = "access_token",
                });
            }
        }

        // Non-JWT tokens (e.g. refresh tokens) are not introspectable in this implementation.
        return Ok(new TokenIntrospectionResponse { Active = false });
    }

    private static OAuthGrantDto MapToDto(OAuthGrantInfo info) => new()
    {
        Id = info.Id,
        GrantType = info.GrantType,
        ClientId = info.ClientId,
        ClientDisplayName = info.ClientDisplayName,
        IsKnownClient = info.IsKnownClient,
        FollowerSubjectId = info.FollowerSubjectId,
        FollowerName = info.FollowerName,
        FollowerEmail = info.FollowerEmail,
        Scopes = info.Scopes,
        Label = info.Label,
        CreatedAt = info.CreatedAt,
        LastUsedAt = info.LastUsedAt,
        LastUsedUserAgent = info.LastUsedUserAgent,
        LimitTo24Hours = info.LimitTo24Hours,
    };
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

/// <summary>
/// Consent approval request (submitted by the consent page)
/// </summary>
public class ConsentApprovalRequest
{
    [FromForm(Name = "client_id")]
    public string ClientId { get; set; } = string.Empty;

    [FromForm(Name = "redirect_uri")]
    public string RedirectUri { get; set; } = string.Empty;

    [FromForm(Name = "scope")]
    public string? Scope { get; set; }

    [FromForm(Name = "state")]
    public string? State { get; set; }

    [FromForm(Name = "code_challenge")]
    public string CodeChallenge { get; set; } = string.Empty;

    [FromForm(Name = "approved")]
    public bool Approved { get; set; }

    /// <summary>
    /// When true, limits data access to 24 hours from the grant creation time.
    /// </summary>
    [FromForm(Name = "limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Client info response for the consent page
/// </summary>
public class OAuthClientInfoResponse
{
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsKnown { get; set; }
    public string? Homepage { get; set; }
}

/// <summary>
/// Device approval request (submitted by the device approval page)
/// </summary>
public class DeviceApprovalRequest
{
    [FromForm(Name = "user_code")]
    public string UserCode { get; set; } = string.Empty;

    [FromForm(Name = "approved")]
    public bool Approved { get; set; }
}

/// <summary>
/// DTO representing an OAuth grant for the management UI
/// </summary>
public class OAuthGrantDto
{
    public Guid Id { get; set; }
    public string GrantType { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string? ClientDisplayName { get; set; }
    public bool IsKnownClient { get; set; }
    public Guid? FollowerSubjectId { get; set; }
    public string? FollowerName { get; set; }
    public string? FollowerEmail { get; set; }
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedUserAgent { get; set; }
    /// <summary>
    /// When true, this grant only allows access to data from the last 24 hours
    /// (rolling window from each request time).
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Response containing a list of OAuth grants
/// </summary>
public class OAuthGrantListResponse
{
    public List<OAuthGrantDto> Grants { get; set; } = new();
}

/// <summary>
/// Request to create a follower grant (share data with another user)
/// </summary>
public class CreateFollowerGrantRequest
{
    public string FollowerEmail { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
    public string? TemporaryPassword { get; set; }
    public string? FollowerDisplayName { get; set; }
}

/// <summary>
/// Request to update an existing grant's label and/or scopes
/// </summary>
public class UpdateGrantRequest
{
    public string? Label { get; set; }
    public List<string>? Scopes { get; set; }
}

/// <summary>
/// DTO for a data owner that the current user can view as a follower
/// </summary>
public class FollowerTargetDto
{
    public Guid SubjectId { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
}

/// <summary>
/// Response containing a list of follower targets
/// </summary>
public class FollowerTargetListResponse
{
    public List<FollowerTargetDto> Targets { get; set; } = new();
}

/// <summary>
/// Token introspection response (RFC 7662)
/// </summary>
public class TokenIntrospectionResponse
{
    public bool Active { get; set; }
    public string? Scope { get; set; }
    public string? ClientId { get; set; }
    public string? Sub { get; set; }
    public long? Exp { get; set; }
    public long? Iat { get; set; }
    public string? Jti { get; set; }
    public string? TokenType { get; set; }
}

/// <summary>
/// Request to create a follower invite link
/// </summary>
public class CreateInviteRequest
{
    /// <summary>
    /// Scopes to grant when the invite is accepted
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Optional label for the grant (e.g., "Mom", "Endocrinologist")
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Days until the invite expires (default: 7)
    /// </summary>
    public int? ExpiresInDays { get; set; }

    /// <summary>
    /// Maximum number of times the invite can be used (null = unlimited)
    /// </summary>
    public int? MaxUses { get; set; }

    /// <summary>
    /// When true, grants created from this invite will only allow access to
    /// the last 24 hours of data (rolling window from each request time).
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Response after creating an invite
/// </summary>
public class CreateInviteResponse
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public string InviteUrl { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// DTO for an invite in list responses
/// </summary>
public class InviteDto
{
    public Guid Id { get; set; }
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsValid { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public bool LimitTo24Hours { get; set; }
    public List<InviteUsageDto> UsedBy { get; set; } = new();
}

/// <summary>
/// DTO for invite usage information
/// </summary>
public class InviteUsageDto
{
    public Guid FollowerSubjectId { get; set; }
    public string? FollowerName { get; set; }
    public string? FollowerEmail { get; set; }
    public DateTime UsedAt { get; set; }
}

/// <summary>
/// Response containing a list of invites
/// </summary>
public class InviteListResponse
{
    public List<InviteDto> Invites { get; set; } = new();
}

/// <summary>
/// Response for invite info (for the accept page)
/// </summary>
public class InviteInfoResponse
{
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsValid { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Response after accepting an invite
/// </summary>
public class AcceptInviteResponse
{
    public bool Success { get; set; }
    public Guid? GrantId { get; set; }
}

#endregion
