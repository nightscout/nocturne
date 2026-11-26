using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Sign-in with an external OpenID Connect provider. A caller can list the configured providers, sign in,
/// link and unlink provider identities, refresh the session, log out, and read the current session.
/// </summary>
/// <remarks>
/// This controller orchestrates the full OIDC lifecycle:
/// <list type="bullet">
///   <item><description>Login: Redirects to the IdP, handles the callback, issues session cookies.</description></item>
///   <item><description>Link/Unlink: Attaches or detaches external identities to an existing subject.</description></item>
///   <item><description>Refresh: Rotates access/refresh tokens via cookie-based refresh.</description></item>
///   <item><description>Logout: Revokes the session and optionally initiates RP-initiated logout.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="IOidcAuthService"/>
/// <seealso cref="IOidcProviderService"/>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IAuthAuditService"/>
/// <seealso cref="OidcOptions"/>
[ApiController]
[Route("api/auth/oidc")]
[System.ComponentModel.DisplayName("OIDC Sign-In")]
[Tags("Authentication")]
public class OidcController : ControllerBase
{
    private readonly IOidcAuthService _authService;
    private readonly IOidcProviderService _providerService;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly RecoverySession _recoverySession;
    private readonly OidcOptions _options;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Creates a new instance of OidcController
    /// </summary>
    public OidcController(
        IOidcAuthService authService,
        IOidcProviderService providerService,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        ITenantMemberService tenantMemberService,
        RecoverySession recoverySession,
        IOptions<OidcOptions> options,
        IOptions<BaseDomainOptions> baseDomainOptions,
        ILogger<OidcController> logger
    )
    {
        _authService = authService;
        _providerService = providerService;
        _subjectService = subjectService;
        _auditService = auditService;
        _tenantMemberService = tenantMemberService;
        _recoverySession = recoverySession;
        _options = options.Value;
        _baseDomain = baseDomainOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get available OIDC providers for login.
    /// </summary>
    /// <returns>List of enabled <see cref="OidcProviderInfo"/> for display on the login page.</returns>
    /// <response code="200">List of enabled OIDC providers.</response>
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
                ButtonForegroundColor = ButtonColorContrast.ForegroundFor(p.ButtonColor),
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
        if (!string.IsNullOrEmpty(returnUrl) && !_baseDomain.IsValidReturnUrl(returnUrl))
        {
            return BadRequest(new { error = "invalid_return_url", message = "Invalid return URL" });
        }

        try
        {
            var tenantSlug = (HttpContext.GetTenantContext())?.Slug;
            var authRequest = await _authService.GenerateAuthorizationUrlAsync(provider, returnUrl, tenantSlug: tenantSlug);

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
    /// Handle OIDC callback from provider.
    /// Exchanges authorization code for tokens and creates session. On success, sets session cookies, or a step-up cookie when a second factor is pending, and redirects. A provider or state failure redirects to the error page, a user who is not a member goes to the login page with the reason (or back to the tenant-creation or join link the sign-in started from), and a missing <c>code</c> or <c>state</c> returns 400.
    /// </summary>
    /// <param name="code">Authorization code from provider.</param>
    /// <param name="state">State parameter for CSRF verification.</param>
    /// <param name="error">Error code from provider (if any).</param>
    /// <param name="error_description">Error description from provider.</param>
    /// <returns>Redirect to return URL with session cookie set.</returns>
    /// <remarks>
    /// Flow: verifies state cookie against the state parameter, exchanges code for tokens
    /// via <see cref="IOidcAuthService.HandleCallbackAsync"/>, whose <see cref="SignInCompletion"/>
    /// writes the <see cref="AuthAuditEventType.Login"/> row, then sets the cookies the outcome calls for.
    /// On failure, logs <see cref="AuthAuditEventType.FailedAuth"/> and redirects to the error page.
    /// </remarks>
    /// <response code="302">Redirect to return URL on success, or error page on failure.</response>
    /// <response code="400">Missing code or state parameters.</response>
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

        // The callback runs on the tenant subdomain (ApexCallbackRelayMiddleware has already
        // bounced apex callbacks to {slug}.{baseDomain}), so the resolved tenant is the one being
        // logged into. Pass it through so a session is only issued to a member of that tenant.
        var currentTenantId = (HttpContext.GetTenantContext())?.TenantId;

        // Handle the callback
        var result = await _authService.HandleCallbackAsync(
            code,
            state,
            expectedState,
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent,
            currentTenantId,
            Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName]
        );

        // The sign-in outcome reaches here flattened into the result; the outcomes rebuilt from it
        // below only say which cookies to write.
        if (result.StepUpToken is { } stepUpToken)
        {
            Response.SetTotpStepUpCookie(stepUpToken, _options);
            await HttpContext.ApplySignInAsync(
                new SignInOutcome.SecondFactorRequired(
                    result.SubjectId!.Value, stepUpToken, SignInMethod.Oidc, result.ProviderId),
                _options);

            return Redirect(
                $"/auth/login?returnUrl={Uri.EscapeDataString(result.ReturnUrl ?? "/")}");
        }

        if (!result.Success)
        {
            // Authenticated identity that is not a member of this tenant: issue no session and
            // send them to the tenant login page, which offers the request-membership option
            // when the tenant allows access requests.
            if (result.IsAccessDenied)
            {
                if (!result.RefusalAudited)
                {
                    await _auditService.LogAsync(AuthAuditEventType.FailedAuth, result.SubjectId, success: false,
                        ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
                        errorMessage: "not_a_member",
                        detailsJson: NotAMemberDetails(result));
                }

                return RedirectToRefusal(result);
            }

            _logger.LogWarning(
                "OIDC callback failed: {Error} - {Description}",
                result.Error,
                result.ErrorDescription
            );

            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, targetSubjectId: null, success: false,
                ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
                errorMessage: result.ErrorDescription,
                detailsJson: JsonSerializer.Serialize(new { method = "oidc" }));

            return RedirectToError(
                result.Error ?? "callback_failed",
                result.ErrorDescription ?? "Authentication failed"
            );
        }

        var tokens = result.Tokens!;
        await HttpContext.ApplySignInAsync(
            new SignInOutcome.SignedIn(
                new SessionTokenPair(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn, tokens.SubjectId),
                result.UsedTrustedDevice,
                SignInMethod.Oidc,
                result.ProviderId),
            _options);

        _logger.LogInformation(
            "OIDC login successful for subject {SubjectId}",
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
    [RequireInteractiveSession]
    [DenyDemoSubject]
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

        if (await HttpContext.IsCallerSubjectInactiveAsync(HttpContext.RequestAborted))
            return Challenge();

        if (!string.IsNullOrEmpty(returnUrl) && !_baseDomain.IsValidReturnUrl(returnUrl))
            return BadRequest(new { error = "invalid_return_url", message = "Invalid return URL" });

        try
        {
            var tenantSlug = (HttpContext.GetTenantContext())?.Slug;
            var req = await _authService.GenerateLinkAuthorizationUrlAsync(
                provider, auth.SubjectId.Value, returnUrl, tenantSlug);
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
    /// Starts linking an identity provider under a recovery session, as that session's one enrolment. Answers the provider's authorisation URL for the browser to open; the provider returns to the link callback.
    /// </summary>
    /// <remarks>
    /// A POST, not a GET, because the recovery cookie is SameSite=Lax (<see cref="RecoverySession"/>)
    /// and rides a cross-site top-level GET: a GET start would let another site begin a link while
    /// the browser holds an identity-provider session the other site chose.
    /// </remarks>
    [HttpPost("recovery/link")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(OidcRecoveryLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OidcRecoveryLinkResponse>> StartRecoveryLink([FromBody] OidcRecoveryLinkRequest request)
    {
        if (!string.IsNullOrEmpty(request.ReturnUrl) && !_baseDomain.IsValidReturnUrl(request.ReturnUrl))
            return Problem(detail: "Invalid return URL", statusCode: StatusCodes.Status400BadRequest, title: "Bad Request");

        var subjectId = await _recoverySession.ReadSubjectAsync(HttpContext, HttpContext.RequestAborted);
        if (subjectId is null)
            return Problem(detail: "A recovery session is required", statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

        OidcAuthorizationRequest authRequest;
        try
        {
            // The slug of the host, not of the resolved tenant: the apex relay sends the callback
            // to that subdomain, and the apex of a single-tenant install serves no subdomain.
            authRequest = await _authService.GenerateRecoveryLinkAuthorizationUrlAsync(
                request.ProviderId, subjectId.Value, request.ReturnUrl,
                SubdomainParser.Extract(Request.Host.Host, _baseDomain.BaseDomain));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to generate a recovery link authorization URL");
            return Problem(detail: "This provider is not available", statusCode: StatusCodes.Status400BadRequest, title: "Bad Request");
        }

        SetLinkStateCookie(authRequest.State, authRequest.ExpiresAt);
        return Ok(new OidcRecoveryLinkResponse { AuthorizationUrl = authRequest.AuthorizationUrl });
    }

    /// <summary>
    /// Handle the OIDC link callback. Verifies the authorisation code against the IdP, then attaches the external identity to the currently-authenticated subject, or under a recovery session to that session's subject. Does NOT issue new session cookies.
    /// </summary>
    /// <remarks>
    /// The two kinds of state are not interchangeable: a link state completes only with a signed-in
    /// session, and a recovery state only with the recovery session it was started under
    /// (<see cref="OidcAuthService.AttachVerifiedIdentityAsync"/>). A recovery link spends the
    /// session and then sends the browser through the ordinary sign-in with the provider just
    /// linked, so the member arrives signed in through that sign-in's own checks, second factor
    /// included. Its return URL names the identity added, so the page can offer to remove older
    /// sign-in methods; an identity the account already had was not added and is not named, but
    /// still ends the session. A refused link, such as an identity already linked to someone else, leaves
    /// the session usable.
    /// <para>
    /// Anonymous and allowed during setup because the recovery session is a cookie, not an auth
    /// context, and is used most when the site is in recovery mode. The link branch refuses an
    /// unauthenticated caller in its body.
    /// </para>
    /// </remarks>
    [HttpGet("link/callback")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description)
    {
        if (!string.IsNullOrEmpty(state) && _authService.IsRecoveryState(state))
            return await RecoveryLinkCallbackAsync(code, state, error, error_description);

        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
        {
            ClearLinkStateCookie();
            return Redirect("/auth/login?returnUrl=/settings/account");
        }

        // An ended session's access token still reads as signed in to the login page, which would
        // send the person straight back to settings, so it is told to sign in again instead.
        if (await HttpContext.IsCallerSubjectInactiveAsync(HttpContext.RequestAborted))
        {
            ClearLinkStateCookie();
            return RedirectToError("callback_failed", "Your session has ended. Sign in again.");
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
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent);

        if (!result.Success)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, auth.SubjectId, success: false,
                ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
                errorMessage: result.ErrorDescription,
                detailsJson: JsonSerializer.Serialize(new { method = "oidc_link" }));

            return RedirectToError(result.Error ?? "link_failed", result.ErrorDescription ?? "Link failed");
        }

        await _auditService.LogAsync(AuthAuditEventType.OidcIdentityLinked, auth.SubjectId, success: true,
            ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
            detailsJson: JsonSerializer.Serialize(new { identityId = result.IdentityId }));

        _logger.LogInformation(
            "OIDC identity {IdentityId} linked to subject {SubjectId}",
            result.IdentityId, auth.SubjectId);

        var returnUrl = result.ReturnUrl ?? "/settings/account";
        var separator = returnUrl.Contains('?') ? '&' : '?';
        return Redirect($"{returnUrl}{separator}linked=success");
    }

    private async Task<IActionResult> RecoveryLinkCallbackAsync(
        string? code, string state, string? error, string? errorDescription)
    {
        ClearLinkStateCookie();
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("OIDC provider returned error on recovery link callback: {Error}", error);
            return RedirectToError(error, errorDescription ?? "Link failed", fromRecovery: true);
        }

        var expectedState = Request.Cookies[_options.Cookie.LinkStateCookieName];
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(expectedState))
            return RedirectToError("invalid_state", "Link state not found. Start the link again.", fromRecovery: true);

        var enrolment = await _recoverySession.EnrolAsync(
            HttpContext,
            (subjectId, _) => _authService.HandleRecoveryLinkCallbackAsync(code, state, expectedState, subjectId),
            result => result.Success,
            HttpContext.RequestAborted);

        if (enrolment is not { State: RecoveryEnrolmentState.Enrolled, Result: { } linked, SubjectId: { } subjectId })
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, targetSubjectId: null, success: false,
                ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
                errorMessage: enrolment.Result?.ErrorDescription ?? "recovery_session_required",
                detailsJson: JsonSerializer.Serialize(new { method = "oidc_recovery_link" }));

            return enrolment.Result is { } refused
                ? RedirectToError(refused.Error ?? "link_failed", refused.ErrorDescription ?? "Link failed", fromRecovery: true)
                : RedirectToError("recovery_session_required", "The recovery session has ended. Start again.", fromRecovery: true);
        }

        await _auditService.LogAsync(AuthAuditEventType.OidcIdentityLinked, subjectId, success: true,
            ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
            detailsJson: JsonSerializer.Serialize(new { identityId = linked.IdentityId, method = "recovery" }));

        var returnUrl = linked.ReturnUrl ?? "/";
        var separator = returnUrl.Contains('?') ? '&' : '?';
        var signedInUrl = linked.Added
            ? $"{returnUrl}{separator}{RecoveryLinkedIdentityParameter}={linked.IdentityId}"
            : returnUrl;
        return Redirect(
            $"/api/auth/oidc/login?provider={linked.ProviderId}&returnUrl={Uri.EscapeDataString(signedInUrl)}");
    }

    /// <summary>
    /// The query parameter a recovery link's return URL carries the new identity's id in.
    /// </summary>
    public const string RecoveryLinkedIdentityParameter = "linkedIdentity";

    /// <summary>
    /// List OIDC identities linked to the currently-authenticated subject.
    /// </summary>
    [HttpGet("link/identities")]
    [DenyDemoSubject]
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
    /// <param name="identityId">The unique identifier of the OIDC identity to unlink.</param>
    /// <returns>204 on success, 404 if not found, 409 if this is the last primary factor.</returns>
    /// <remarks>
    /// Factor-count enforcement is handled atomically inside <see cref="ISubjectService.TryRemoveOidcIdentityAsync"/>
    /// using a serializable transaction to prevent TOCTOU races between concurrent removals.
    /// Returns <see cref="FactorRemovalResult"/> to distinguish between not-found, last-factor, and success.
    /// <para>
    /// Dropping a primary factor also changes the factor count that
    /// <see cref="PasskeyController.ListCredentials"/> reports, which is what the account page reads to
    /// decide whether a Remove is offered at all. That read lives under another OpenAPI tag, so it is
    /// named by its full operationId — a bare name resolves only within the declaring operation's own tag.
    /// </para>
    /// </remarks>
    /// <response code="204">Identity unlinked successfully.</response>
    /// <response code="401">Not authenticated.</response>
    /// <response code="404">Identity not found.</response>
    /// <response code="409">Cannot remove the last primary sign-in method.</response>
    [HttpDelete("link/identities/{identityId:guid}")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetLinkedIdentities", "Passkey_ListCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnlinkIdentity(Guid identityId)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || !auth.SubjectId.HasValue)
            return Unauthorized();

        // Symmetric factor-count rule is enforced atomically inside the service inside a
        // serializable transaction to prevent TOCTOU races between concurrent removals.
        var result = await _subjectService.TryRemoveOidcIdentityAsync(auth.SubjectId.Value, identityId);
        if (result != FactorRemovalResult.Removed)
            return this.FactorRemovalRefusal(result, "OIDC identity");

        await _auditService.LogAsync(AuthAuditEventType.OidcIdentityUnlinked, auth.SubjectId, success: true,
            ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent,
            detailsJson: JsonSerializer.Serialize(new { identityId }));

        _logger.LogInformation(
            "OIDC identity {IdentityId} unlinked from subject {SubjectId}",
            identityId, auth.SubjectId);

        return NoContent();
    }

    /// <summary>
    /// Refresh the session tokens.
    /// Uses the refresh token to get a new access token. The refresh token is read from the cookie or the <c>Refresh</c> Authorization header. A failed refresh returns 401 and leaves cookies unchanged.
    /// </summary>
    /// <returns>A new <see cref="OidcTokenResponse"/> with rotated tokens.</returns>
    /// <remarks>
    /// The refresh token is read from the cookie or the <c>Refresh</c> Authorization header (for API clients).
    /// On failure, returns 401 without touching cookies — session cookies are domain-wide, so a
    /// deletion issued for a stale token would wipe a newer session the browser obtained concurrently.
    /// Logs <see cref="AuthAuditEventType.TokenRefreshed"/> on success.
    /// </remarks>
    /// <response code="200">Tokens refreshed successfully.</response>
    /// <response code="401">No refresh token found, or refresh token is invalid/expired.</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [AllowDuringSetup]
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
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent
        );

        if (result == null)
        {
            // No cookie clearing on failure: a stale token (e.g. from a restored tab) must
            // not wipe the domain-wide session cookies of a newer concurrent session.
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
            ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent);

        return Ok(result);
    }

    /// <summary>
    /// Logout and end the session. Clears all session cookies and ends the session the presented refresh token belongs to and the session the caller is signed in with. When <c>providerId</c> names a provider that supports RP-initiated logout, the response includes a provider logout URL.
    /// </summary>
    /// <param name="providerId">Provider ID for RP-initiated logout (optional).</param>
    /// <returns>A <see cref="LogoutResponse"/> indicating success and an optional provider logout URL.</returns>
    /// <remarks>
    /// Clears all session cookies (access token, refresh token, IsAuthenticated).
    /// Ends the session through <see cref="IOidcAuthService.LogoutAsync"/>.
    /// When <paramref name="providerId"/> is supplied and the provider supports RP-initiated logout,
    /// the response includes a <c>ProviderLogoutUrl</c> for the frontend to redirect to.
    /// </remarks>
    /// <response code="200">Logout successful.</response>
    [HttpPost("logout")]
    [RemoteCommand]
    [ProducesResponseType(typeof(LogoutResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogoutResponse>> Logout([FromQuery] Guid? providerId = null)
    {
        var refreshToken = GetRefreshToken();
        var authContext = HttpContext.GetAuthContext();
        var signedIn = authContext is { SubjectId: { } subjectId, SessionId: { Length: > 0 } sessionId }
            && CallerSessionLiveness.IsSessionCaller(authContext)
                ? new RefreshTokenSession(subjectId, sessionId)
                : null;

        var result = string.IsNullOrEmpty(refreshToken) && signedIn is null
            ? OidcLogoutResult.Succeeded()
            : await _authService.LogoutAsync(refreshToken, signedIn, providerId);

        // Clear session cookies
        ClearSessionCookies();

        await _auditService.LogAsync(AuthAuditEventType.Logout, authContext?.SubjectId, success: true,
            ipAddress: HttpContext.GetClientAddress(), userAgent: Request.Headers.UserAgent);

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
    /// Get current user information.
    /// </summary>
    /// <returns>An <see cref="OidcUserInfo"/> with the user's profile from the current session.</returns>
    /// <response code="200">User information retrieved.</response>
    /// <response code="401">Not authenticated or user not found.</response>
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
    /// Get current session information including authentication status, roles, and permissions.
    /// An unauthenticated request returns 200 with <c>IsAuthenticated</c> false rather than 401.
    /// </summary>
    /// <returns>A <see cref="SessionInfo"/> describing the current session state.</returns>
    /// <remarks>
    /// Returns <c>IsAuthenticated = false</c> for unauthenticated requests (does not return 401).
    /// This allows the frontend to check auth status without triggering redirect logic. A session
    /// caller that a mint would refuse (<see cref="HttpContextExtensions.IsSessionCallerInactiveAsync"/>)
    /// reads as not authenticated, so the web offers it the sign-in that the refusal asks for.
    /// </remarks>
    /// <response code="200">Session information (always returns 200).</response>
    [HttpGet("session")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [ProducesResponseType(typeof(SessionInfo), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionInfo>> GetSession(
        [FromServices] ITenantService tenants, CancellationToken ct = default)
    {
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated
            || await HttpContext.IsSessionCallerInactiveAsync(ct))
        {
            return Ok(new SessionInfo { IsAuthenticated = false });
        }

        var userInfo = authContext.SubjectId.HasValue
            ? await _authService.GetUserInfoAsync(authContext.SubjectId.Value)
            : null;

        // Surface the subject's roles within the CURRENT tenant rather than the global
        // subject roles carried on the auth context — the latter are empty for ordinary
        // members, so the account/profile UI showed "No roles assigned". Platform-access
        // grants (a non-member operator) and service auth keep their context roles.
        var roles = authContext.Roles;
        if (authContext is { SubjectId: not null, TenantId: not null }
            && authContext.AuthType is not (AuthType.PlatformAccess or AuthType.ApiKey or AuthType.InstanceKey))
        {
            roles = await _tenantMemberService.GetMemberRoleNamesAsync(
                authContext.SubjectId.Value, authContext.TenantId.Value);
        }

        return Ok(
            new SessionInfo
            {
                IsAuthenticated = true,
                SubjectId = authContext.SubjectId,
                Name = authContext.SubjectName ?? userInfo?.Name,
                Email = authContext.Email ?? userInfo?.Email,
                Roles = roles,
                Permissions = authContext.Permissions,
                ExpiresAt = authContext.ExpiresAt,
                PreferredLanguage = userInfo?.PreferredLanguage,
                Preferences = userInfo?.Preferences,
                DefaultGlucoseUnits = authContext.TenantId is { } tenantId
                    ? await tenants.GetDefaultGlucoseUnitsAsync(tenantId, ct)
                    : null,
                IsPlatformAdmin = authContext.IsPlatformAdmin,
                IsPlatformAccessGrant = authContext.AuthType == AuthType.PlatformAccess,
                IsHandedOff = authContext.IsHandedOff,
                AvatarUrl = userInfo?.AvatarUrl,
                Grant = await MyPermissionsController.TryBuildAsync(HttpContext),
            }
        );
    }

    #region Private Helper Methods

    private void SetStateCookie(string state, DateTimeOffset expiresAt) =>
        Response.SetStateCookie(_options.Cookie.StateCookieName, state, expiresAt, _options);

    private void ClearStateCookie() =>
        Response.ClearStateCookie(_options.Cookie.StateCookieName, _options);

    private void SetLinkStateCookie(string state, DateTimeOffset expiresAt) =>
        Response.SetStateCookie(_options.Cookie.LinkStateCookieName, state, expiresAt, _options);

    private void ClearLinkStateCookie() =>
        Response.ClearStateCookie(_options.Cookie.LinkStateCookieName, _options);

    private void SetSessionCookies(OidcTokenResponse tokens) =>
        Response.SetSessionCookies(
            tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt, _options);

    private void ClearSessionCookies() => Response.ClearSessionCookies(_options);

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
        return Request.GetAuthorizationCredential("Refresh");
    }

    /// <summary>
    /// Redirect to an error page
    /// </summary>
    /// <param name="error">The error code the page maps to its message.</param>
    /// <param name="description">The fallback description.</param>
    /// <param name="fromRecovery">
    /// The failure came from linking a provider under a recovery session. A refused link leaves
    /// that session usable, so the page offers the way back to it; a refusal carries the same
    /// codes as an ordinary sign-in, so the code alone cannot tell the page.
    /// </param>
    private IActionResult RedirectToError(string error, string description, bool fromRecovery = false)
    {
        var returnUrl =
            $"/auth/error?error={Uri.EscapeDataString(error)}&description={Uri.EscapeDataString(description)}"
            + (fromRecovery ? "&flow=recovery" : "");
        return Redirect(returnUrl);
    }

    /// <summary>
    /// Sends back a person whose provider sign-in was refused. A sign-in that started from a
    /// tenant-creation link or a join link returns to that link, whose page says why it cannot be
    /// used. Any other goes to the login page with the refusal's reason, which it words for the host,
    /// and the return URL; the page also surfaces the request-membership option when the tenant
    /// allows access requests.
    /// </summary>
    private IActionResult RedirectToRefusal(OidcCallbackResult refused)
    {
        var startedFromLink =
            OidcAuthService.TokenFromReturnUrl(
                refused.ReturnUrl,
                ITenantCreationInviteService.StartPath,
                ITenantCreationInviteService.TokenQueryParameter) != null
            || OidcAuthService.TokenFromReturnUrl(
                refused.ReturnUrl,
                IMemberInviteService.JoinPath,
                IMemberInviteService.TokenQueryParameter) != null;
        if (startedFromLink)
            return Redirect(refused.ReturnUrl!);

        var login = $"/auth/login?reason={Uri.EscapeDataString(refused.Error!)}";
        return Redirect(string.IsNullOrEmpty(refused.ReturnUrl)
            ? login
            : $"{login}&returnUrl={Uri.EscapeDataString(refused.ReturnUrl)}");
    }

    /// <summary>
    /// Names the provider always. The person is the row's subject when there is one, else a digest
    /// of the external identity, so the row never holds the identity itself.
    /// </summary>
    internal static string NotAMemberDetails(OidcCallbackResult result) =>
        result.ExternalIdentityDigest is { } digest
            ? JsonSerializer.Serialize(new
            {
                method = "oidc",
                reason = "not_a_member",
                providerId = result.ProviderId,
                externalIdentityDigest = digest,
            })
            : JsonSerializer.Serialize(new
            {
                method = "oidc",
                reason = "not_a_member",
                providerId = result.ProviderId,
            });

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

    /// <summary>
    /// Text colour legible on <see cref="ButtonColor"/>; null when that is not a hex colour,
    /// in which case the button keeps its themed style.
    /// </summary>
    public string? ButtonForegroundColor { get; set; }
}

/// <summary>The provider a recovery session links, and where to return after signing in with it.</summary>
public class OidcRecoveryLinkRequest
{
    public Guid ProviderId { get; set; }

    public string? ReturnUrl { get; set; }
}

/// <summary>The provider's authorisation URL, for the browser to open.</summary>
public class OidcRecoveryLinkResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
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
    /// Per-user display preferences (units, time format, theme, chart style, etc.).
    /// Used for server-side hydration so first paint matches the user's saved choices.
    /// </summary>
    public UserDisplayPreferences? Preferences { get; set; }

    /// <summary>
    /// The tenant's default glucose units ("mg/dl" or "mmol"), shown to a member who has chosen
    /// none of their own. Kept apart from <see cref="Preferences"/> so an inherited default is
    /// never taken for the member's own saved choice. Null outside a tenant or before the owner
    /// chooses.
    /// </summary>
    public string? DefaultGlucoseUnits { get; set; }

    /// <summary>
    /// Whether this subject has platform-level admin access
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// Whether this session is a short-lived platform-admin access grant on a tenant the
    /// subject is NOT a member of (as opposed to ordinary membership). Authoritative signal
    /// for the "platform admin access" indicator in the UI.
    /// </summary>
    public bool IsPlatformAccessGrant { get; set; }

    /// <summary>
    /// Whether this session was opened from a sign-in link someone else issued. It reads and writes
    /// the member's data, but managing sign-in methods, credentials and sharing waits until the
    /// member signs in on it, which the web offers up front.
    /// </summary>
    public bool IsHandedOff { get; set; }

    /// <summary>
    /// URL to the subject's avatar image
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// The caller's grant on the resolved tenant, as <c>GET /api/v4/me/permissions</c> answers it;
    /// null where that endpoint would refuse the caller.
    /// </summary>
    public MyPermissionsResponse? Grant { get; set; }
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
