using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.RateLimiting;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Authenticator app codes (TOTP) as a second step of sign-in. A caller can set up an
/// authenticator, verify it, and list and remove authenticators. A code completes a sign-in that a
/// passkey or an external provider started. A code alone never signs in. A caller who presents a
/// code can trust the device, so later sign-ins from it skip the code.
/// </summary>
/// <remarks>
/// TOTP is never a sole factor: <see cref="Setup"/> requires at least one primary auth factor
/// (passkey or OIDC link) to already be configured, and <see cref="Login"/> requires a step-up
/// token minted by a completed primary factor, so a code on its own can never produce a session.
/// Both primary factors, <c>PasskeyController.LoginComplete</c> and
/// <c>OidcAuthService.CompleteLoginAsync</c>, withhold the session from a subject with an
/// authenticator enrolled and mint that token instead, except on a device the subject trusted
/// here with an earlier code (<see cref="ITotpService.GetSecondFactorRequirementAsync"/>).
/// </remarks>
/// <seealso cref="ITotpService"/>
/// <seealso cref="SignInCompletion"/>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IAuthAuditService"/>
[ApiController]
[Route("api/auth/totp")]
[System.ComponentModel.DisplayName("TOTP")]
[Tags("Authentication")]
[AllowDuringSetup]
public class TotpController : ControllerBase
{
    /// <summary>
    /// Returned for every rejected step-up so the response does not distinguish an expired token
    /// from a wrong code, or reveal whether an account exists.
    /// </summary>
    private const string CodeNotAccepted = "That code wasn't accepted. Please sign in again.";

    /// <summary>
    /// The 403 <c>detail</c> for a verified sign-in that this host will not admit. Both factors
    /// passed, so the caller is the account holder and learns nothing about anyone else.
    /// </summary>
    public const string NotAMember = "not_a_member";

    private readonly ITotpService _totpService;
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly SignInCompletion _signIn;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly OidcOptions _oidcOptions;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<TotpController> _logger;

    /// <summary>
    /// Creates a new instance of TotpController
    /// </summary>
    public TotpController(
        ITotpService totpService,
        ITrustedDeviceService trustedDevices,
        SignInCompletion signIn,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        ITenantAccessor tenantAccessor,
        IOptions<OidcOptions> oidcOptions,
        IOptions<BaseDomainOptions> baseDomainOptions,
        ILogger<TotpController> logger)
    {
        _totpService = totpService;
        _trustedDevices = trustedDevices;
        _signIn = signIn;
        _subjectService = subjectService;
        _auditService = auditService;
        _tenantAccessor = tenantAccessor;
        _oidcOptions = oidcOptions.Value;
        _baseDomain = baseDomainOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// The 400 for a refused enrolment. <c>detail</c> carries the <see cref="TotpSetupFailure"/>
    /// value rather than a sentence: the generated remote wrapper forwards <c>detail</c> and drops
    /// the rest of the body, so it is the only channel to the browser, and the wording is the web
    /// app's to choose.
    /// </summary>
    private ObjectResult Refused(TotpSetupFailure failure)
        => Problem(detail: failure.ToString(), statusCode: 400, title: "Bad Request");

    /// <summary>
    /// Generate TOTP setup data including provisioning URI and secret. Requires at least one primary auth factor (passkey or OIDC link) already configured.
    /// </summary>
    /// <returns>A <see cref="TotpSetupResponse"/> containing the provisioning URI, base32 secret, and challenge token.</returns>
    /// <remarks>
    /// Requires at least one primary auth factor (passkey or OIDC link) to be configured.
    /// The returned <see cref="TotpSetupResponse.ChallengeToken"/> must be passed to
    /// <see cref="VerifySetup"/> along with a valid 6-digit code to complete setup.
    /// </remarks>
    /// <response code="200">TOTP setup data generated.</response>
    /// <response code="400">Setup refused; <c>detail</c> is a <c>TotpSetupFailure</c> value.</response>
    /// <response code="401">Not authenticated.</response>
    [HttpPost("setup")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand]
    [ProducesResponseType(typeof(TotpSetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TotpSetupResponse>> Setup()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        if (await HttpContext.IsCallerSubjectInactiveAsync(HttpContext.RequestAborted))
            return Challenge();

        var subject = await _subjectService.GetSubjectByIdAsync(auth.SubjectId.Value);
        if (subject == null)
            return Refused(TotpSetupFailure.SubjectNotFound);

        // TOTP is a second factor; require at least one primary factor (passkey or OIDC link)
        // so a user cannot end up with only TOTP configured.
        var primaryFactorCount = await _subjectService.CountPrimaryAuthFactorsAsync(auth.SubjectId.Value);
        if (primaryFactorCount < 1)
            return Refused(TotpSetupFailure.NoPrimaryFactor);

        var result = await _totpService.GenerateSetupAsync(auth.SubjectId.Value, subject.Name);

        return Ok(new TotpSetupResponse
        {
            ProvisioningUri = result.ProvisioningUri,
            Base32Secret = result.Base32Secret,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Verify a TOTP code to complete authenticator setup.
    /// </summary>
    /// <param name="request">A <see cref="TotpVerifySetupRequest"/> containing the 6-digit code, label, and challenge token.</param>
    /// <returns>A <see cref="TotpVerifySetupResponse"/> with the new credential ID on success.</returns>
    /// <exception cref="TotpSetupException">Thrown when the challenge token is invalid or the code does not match.</exception>
    /// <response code="200">TOTP setup verified and credential created.</response>
    /// <response code="400">Invalid code or challenge token; <c>detail</c> is a <c>TotpSetupFailure</c> value.</response>
    /// <response code="401">Not authenticated.</response>
    [HttpPost("verify-setup")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(typeof(TotpVerifySetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TotpVerifySetupResponse>> VerifySetup([FromBody] TotpVerifySetupRequest request)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        if (await HttpContext.IsCallerSubjectInactiveAsync(HttpContext.RequestAborted))
            return Challenge();

        try
        {
            var result = await _totpService.CompleteSetupAsync(request.Code, request.Label, request.ChallengeToken);

            return Ok(new TotpVerifySetupResponse
            {
                CredentialId = result.CredentialId,
                Success = true,
            });
        }
        catch (TotpSetupException ex)
        {
            return Refused(ex.Failure);
        }
    }

    /// <summary>
    /// List all TOTP credentials for the authenticated user.
    /// </summary>
    /// <returns>A list of <see cref="TotpCredentialDto"/> for the current subject.</returns>
    /// <response code="200">List of TOTP credentials.</response>
    /// <response code="401">Not authenticated.</response>
    [HttpGet]
    [DenyDemoSubject]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TotpCredentialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<TotpCredentialDto>>> ListCredentials()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        var credentials = await _totpService.GetCredentialsAsync(auth.SubjectId.Value);

        return Ok(credentials.Select(c => new TotpCredentialDto
        {
            Id = c.Id,
            Label = c.Label,
            CreatedAt = c.CreatedAt,
            LastUsedAt = c.LastUsedAt,
        }).ToList());
    }

    /// <summary>
    /// Remove a TOTP credential by ID. Allowed even when it is the last TOTP credential. Every trusted device of the account stops being trusted.
    /// </summary>
    /// <param name="id">The unique identifier of the TOTP credential to remove.</param>
    /// <returns>204 on success.</returns>
    /// <remarks>
    /// No factor-count guard: TOTP is a second factor, so removing it can never
    /// lock a user out of their primary sign-in method.
    /// </remarks>
    /// <response code="204">Credential removed.</response>
    /// <response code="401">Not authenticated.</response>
    [HttpDelete("{id:guid}")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["ListCredentials", "TrustedDevices_List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveCredential(Guid id)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        // No factor-count guard: TOTP is a second factor, so removing it can never
        // lock a user out of their primary sign-in method.
        var untrusted = await _totpService.RemoveCredentialAsync(id, auth.SubjectId.Value);
        if (untrusted > 0)
        {
            await _auditService.LogAsync(AuthAuditEventType.DeviceTrustRevoked, auth.SubjectId, success: true,
                ipAddress: HttpContext.GetClientAddress(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { all = true, count = untrusted, reason = "authenticator_removed" }));
        }

        return NoContent();
    }

    /// <summary>
    /// Complete sign-in with a TOTP code after a primary factor has been verified. Requires the step-up token from that factor and takes no username. Issues session cookies on success. Rate-limited.
    /// </summary>
    /// <param name="request">A <see cref="TotpLoginRequest"/> containing the step-up token and 6-digit code.</param>
    /// <returns>A <see cref="TotpLoginResponse"/> with access token on success.</returns>
    /// <remarks>
    /// TOTP is a second factor, so this endpoint needs a step-up token from a completed passkey
    /// assertion or OIDC callback — it cannot be reached with a code alone. The token names the subject; a
    /// caller-supplied username is not accepted.
    /// Rate-limited per client address via the "totp-login" policy, and per step-up token and per
    /// account by <see cref="StepUpCodeAttempts"/>: a token's wrong codes spend it, and an account's
    /// wrong codes across its tokens refuse further codes, with the rate-limit response, until the
    /// window ends.
    /// On success: issues session cookies, updates last login time, and logs
    /// <see cref="AuthAuditEventType.Login"/>. On failure: logs <see cref="AuthAuditEventType.FailedAuth"/>.
    /// </remarks>
    /// <response code="200">Login successful with access token.</response>
    /// <response code="400">Invalid step-up token or code.</response>
    /// <response code="403">Both factors passed, but this host does not admit the subject;
    /// <c>detail</c> is <c>not_a_member</c>.</response>
    /// <response code="429">Too many attempts from this client address, or the account has no wrong codes left until
    /// its window ends. <c>Retry-After</c> says when to try again.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("totp-login")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TotpLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TotpLoginResponse>> Login([FromBody] TotpLoginRequest request)
    {
        var ip = HttpContext.GetClientAddress();
        var ua = Request.Headers.UserAgent.ToString();

        TotpLoginResult? result;
        try
        {
            result = await _totpService.VerifyStepUpAsync(request.StepUpToken, request.Code);
        }
        catch (TotpStepUpLockedException ex)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, ex.SubjectId, success: false,
                ipAddress: ip, userAgent: ua,
                detailsJson: JsonSerializer.Serialize(new { method = "totp", reason = "too_many_wrong_codes" }));
            return new RateLimitRejectionResult(ex.RetryAfter);
        }

        if (result == null)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, targetSubjectId: null, success: false,
                ipAddress: ip, userAgent: ua,
                detailsJson: JsonSerializer.Serialize(new { method = "totp" }));
            return Problem(detail: CodeNotAccepted, statusCode: 400, title: "Bad Request");
        }

        // The primary factor decided who may sign in where, and the token carries that decision:
        // redeem it only on the host it was minted for. A membership revoked since then still
        // denies, unless the primary factor admitted this subject on an invite.
        if (!IsMintingHost(result.TenantId))
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, result.SubjectId, success: false,
                ipAddress: ip, userAgent: ua,
                detailsJson: JsonSerializer.Serialize(new { method = "totp", reason = "not_a_member" }));
            return Problem(detail: NotAMember, statusCode: 403, title: "Forbidden");
        }

        SignInAdmission admission = result switch
        {
            { AdmittedByInvite: true } => new SignInAdmission.Invite(result.TenantId),
            { TenantId: { } tenantId } => new SignInAdmission.Member(tenantId),
            _ => new SignInAdmission.Account(),
        };

        var outcome = await _signIn.CompleteAsync(new SignInAttempt(
            result.SubjectId,
            SignInMethod.Totp,
            admission,
            new SecondFactorProof.CodeVerified(result),
            new SessionContext(DeviceDescription: "TOTP", IpAddress: ip, UserAgent: ua)));

        if (outcome is not SignInOutcome.SignedIn signedIn)
            return Problem(detail: NotAMember, statusCode: 403, title: "Forbidden");

        await HttpContext.ApplySignInAsync(signedIn, _oidcOptions);

        if (request.TrustDevice
            && await _trustedDevices.TrustAsync(
                result.SubjectId,
                Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName],
                ua, HttpContext.RequestAborted) is { } grant)
        {
            Response.SetTrustedDeviceCookie(grant, _oidcOptions);

            await _auditService.LogAsync(AuthAuditEventType.DeviceTrustGranted, result.SubjectId, success: true,
                ipAddress: ip, userAgent: ua,
                detailsJson: JsonSerializer.Serialize(new { deviceId = grant.DeviceId, expiresAt = grant.ExpiresAt }));
        }

        return Ok(new TotpLoginResponse
        {
            Success = true,
            AccessToken = signedIn.Session.AccessToken,
            ExpiresIn = signedIn.Session.ExpiresInSeconds,
        });
    }

    /// <summary>
    /// Whether the authenticator step offers to trust the device, and for how long. Anonymous: the
    /// step runs before any session exists.
    /// </summary>
    /// <remarks>
    /// A lifetime that is not a whole number of days is rounded up, so the option is offered
    /// exactly when <see cref="ITrustedDeviceService.Lifetime"/> is above zero.
    /// </remarks>
    /// <response code="200">The policy; <c>days</c> is 0 when trusted devices are turned off.</response>
    [HttpGet("trusted-device-policy")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrustedDevicePolicyResponse), StatusCodes.Status200OK)]
    public ActionResult<TrustedDevicePolicyResponse> GetTrustedDevicePolicy() =>
        Ok(new TrustedDevicePolicyResponse
        {
            Days = Math.Max(0, (int)Math.Ceiling(_trustedDevices.Lifetime.TotalDays)),
        });

    /// <summary>
    /// Whether this request is on the host a step-up token bound to <paramref name="boundTenantId"/>
    /// was minted on.
    /// </summary>
    /// <remarks>
    /// The apex of a single-tenant install is one host with two answers: this endpoint and the
    /// passkey ceremonies resolve it as the sole tenant, while the OIDC callback is served there
    /// without a tenant (<see cref="TenantResolutionMiddleware"/>). So a token minted with no tenant
    /// also matches the apex when it resolves a tenant, and nowhere else that does.
    /// </remarks>
    private bool IsMintingHost(Guid? boundTenantId)
    {
        Guid? hostTenantId = _tenantAccessor.IsResolved ? _tenantAccessor.TenantId : null;
        return boundTenantId == hostTenantId
            || (boundTenantId is null
                && SubdomainParser.Extract(Request.Host.Host, _baseDomain.BaseDomain) is null);
    }

}

#region DTOs

/// <summary>
/// Response containing TOTP setup data
/// </summary>
public class TotpSetupResponse
{
    public string ProvisioningUri { get; set; } = string.Empty;
    public string Base32Secret { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

/// <summary>
/// Request to verify a TOTP code during setup
/// </summary>
public class TotpVerifySetupRequest
{
    [Required, RegularExpression(@"^\d{6}$")]
    public string Code { get; set; } = string.Empty;

    [StringLength(255)]
    public string Label { get; set; } = string.Empty;

    [Required]
    public string ChallengeToken { get; set; } = string.Empty;
}

/// <summary>
/// Response after successful TOTP setup verification
/// </summary>
public class TotpVerifySetupResponse
{
    public Guid CredentialId { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// TOTP credential information
/// </summary>
public class TotpCredentialDto
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// Request to complete sign-in with TOTP. The subject comes from the step-up token, which is
/// issued only after a primary factor has been verified.
/// </summary>
public class TotpLoginRequest
{
    [Required]
    public string StepUpToken { get; set; } = string.Empty;

    [Required, RegularExpression(@"^\d{6}$")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Trust this device, so its later sign-ins skip the code until the trust expires. Ignored when
    /// trusted devices are turned off.
    /// </summary>
    public bool TrustDevice { get; set; }
}

/// <summary>
/// What the authenticator step offers about trusting the device.
/// </summary>
public class TrustedDevicePolicyResponse
{
    /// <summary>How many days a trusted device skips the code. 0 when the option is not offered.</summary>
    public int Days { get; set; }
}

/// <summary>
/// Response after successful TOTP authentication
/// </summary>
public class TotpLoginResponse
{
    public bool Success { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
}

#endregion
