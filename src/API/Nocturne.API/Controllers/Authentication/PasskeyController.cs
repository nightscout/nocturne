using System.Text.Json;
using Fido2NetLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Passkey sign-in, registration and recovery. A caller can register a passkey, sign in with one,
/// and sign in with a recovery code. A signed-in caller can list and delete passkeys, create a new
/// set of recovery codes, and read how many recovery codes remain.
/// </summary>
/// <remarks>
/// Implements WebAuthn/FIDO2 ceremonies.
///
/// Authentication flows:
/// <list type="bullet">
///   <item><description><b>Registration</b> (authenticated, enrols onto the caller's own account): <c>POST /register/options</c> → <c>POST /register/complete</c></description></item>
///   <item><description><b>Discoverable login</b> (no username): <c>POST /login/discoverable/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Non-discoverable login</b> (with username): <c>POST /login/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Recovery:</b> <c>POST /recovery/verify</c> spends one of the member's own recovery codes for a <see cref="RecoverySession"/>.</description></item>
///   <item><description><b>Recovery mode:</b> <c>POST /recovery-mode/redeem</c> spends a code an administrator or the operator issued, or an emailed recovery link, for the same session.</description></item>
///   <item><description><b>Recovery link:</b> <c>POST /recovery-link</c> asks for a recovery link to be emailed to a confirmed address.</description></item>
///   <item><description><b>Initial setup:</b> handled by the setup controller under <c>/api/v4/setup/</c>, not here.</description></item>
///   <item><description><b>Invite acceptance:</b> <c>POST /invite/options</c> → <c>POST /invite/complete</c> using a pre-issued invite token.</description></item>
/// </list>
///
/// Login, invite acceptance and the tenant creation link hand the proved subject to
/// <see cref="SignInCompletion"/>, which issues the session.
///
/// Passkey deletion is guarded by <see cref="ISubjectService.TryRemovePasskeyCredentialAsync"/> which
/// enforces an atomic last-factor check inside a serializable transaction.
/// </remarks>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="IJwtService"/>
/// <seealso cref="ISessionService"/>
/// <seealso cref="IRecoveryCodeService"/>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IAuthAuditService"/>
[ApiController]
[Route("api/auth/passkey")]
[System.ComponentModel.DisplayName("Passkeys")]
[Tags("Authentication")]
[AllowDuringSetup]
public class PasskeyController : ControllerBase
{
    private const string AccessRequestActor = "passkey:access-request";

    private readonly IPasskeyService _passkeyService;
    private readonly SignInCompletion _signIn;
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly RecoverySession _recoverySession;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantService _tenantService;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly ISubjectRepository _subjects;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly INocturneDbContextFactory _dbContextFactory;
    private readonly OidcOptions _oidcOptions;
    private readonly ILogger<PasskeyController> _logger;
    private readonly TimeProvider _time;

    /// <param name="passkeyService">Generates each ceremony's options, verifies its response, lists a subject's passkeys, and removes the passkey of a refused invite.</param>
    /// <param name="signIn">Finishes a passkey login, an invite acceptance and a tenant creation once the
    /// ceremony has proved the subject.</param>
    /// <param name="trustedDevices">Forgets a subject's trusted devices when a recovery code is spent.</param>
    /// <param name="recoveryCodeService">Generates, counts, verifies and voids recovery codes.</param>
    /// <param name="recoverySession">Starts, reads and spends the recovery session a recovery code buys.</param>
    /// <param name="subjectService">Counts a subject's sign-in factors and removes a passkey under the last-factor check.</param>
    /// <param name="auditService">Records the login and recovery outcomes in the auth audit log.</param>
    /// <param name="tenantAccessor">For the id of the request's tenant.</param>
    /// <param name="tenantService">Stored, and read by no action.</param>
    /// <param name="tenantMemberService">For the tenants a subject without credentials is a member of.</param>
    /// <param name="subjects">Reads, creates and updates the subjects the ceremonies enrol or sign in.</param>
    /// <param name="tenants">Reads the request's tenant and marks its onboarding complete.</param>
    /// <param name="members">For the owners an access request is announced to.</param>
    /// <param name="dbContextFactory">For the login codes the ceremonies read beside the subjects.</param>
    /// <param name="oidcOptions">For the cookie settings the session and recovery cookies are written with.</param>
    /// <param name="logger">Logs failed ceremonies and invite enrolments.</param>
    /// <param name="timeProvider">The clock a member's end date is judged by.</param>
    public PasskeyController(
        IPasskeyService passkeyService,
        SignInCompletion signIn,
        ITrustedDeviceService trustedDevices,
        IRecoveryCodeService recoveryCodeService,
        RecoverySession recoverySession,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        ITenantAccessor tenantAccessor,
        ITenantService tenantService,
        ITenantMemberService tenantMemberService,
        ISubjectRepository subjects,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        INocturneDbContextFactory dbContextFactory,
        IOptions<OidcOptions> oidcOptions,
        ILogger<PasskeyController> logger,
        TimeProvider timeProvider)
    {
        _time = timeProvider;
        _passkeyService = passkeyService;
        _signIn = signIn;
        _trustedDevices = trustedDevices;
        _recoveryCodeService = recoveryCodeService;
        _recoverySession = recoverySession;
        _subjectService = subjectService;
        _auditService = auditService;
        _tenantAccessor = tenantAccessor;
        _tenantService = tenantService;
        _tenantMemberService = tenantMemberService;
        _subjects = subjects;
        _tenants = tenants;
        _members = members;
        _dbContextFactory = dbContextFactory;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generate registration options for a new passkey credential on the caller's own account. The account comes from the caller's credentials; the request cannot name another.
    /// </summary>
    /// <remarks>
    /// The subject comes from the caller's credentials, never from the request: a caller-supplied
    /// subject id would let anyone enrol their own authenticator onto another account.
    /// </remarks>
    [HttpPost("register/options")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PasskeyOptionsResponse>> RegisterOptions([FromBody] PasskeyRegisterOptionsRequest request)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        if (await ResolveRegistrationSubjectAsync(HttpContext.RequestAborted) is not { } registering)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        if (string.IsNullOrEmpty(request.Username))
        {
            return Problem(detail: "Username is required", statusCode: 400, title: "Bad Request");
        }

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            registering.SubjectId, request.Username, PasskeyRegistrationFlow.OwnAccount);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Resolves which subject a registration ceremony is allowed to bind a credential to.
    /// </summary>
    /// <returns>
    /// The subject to register against and whether the caller is signed in as it, or
    /// <see langword="null"/> when none qualifies.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The request deliberately gets no say in this. The subject id is sealed into the challenge
    /// token here, so honouring a caller-supplied one let an anonymous caller bind their own
    /// authenticator to any subject whose id they knew and then log in as them.
    /// </para>
    /// <para>
    /// Two flows reach these endpoints: adding a passkey to your own account, and enrolling one
    /// under a <see cref="RecoverySession"/>. Every other enrolment has its own endpoint with its
    /// own precondition: the first owner, invite acceptance, an access request, and an account
    /// with no sign-in method left. None of them resolve a subject here, and none redeem a
    /// challenge minted here: each challenge is bound to the flow that minted it
    /// (<see cref="PasskeyRegistrationFlow"/>).
    /// </para>
    /// <para>
    /// A caller whose subject is inactive or whose session has ended is not signed in here
    /// (<see cref="HttpContextExtensions.IsCallerSubjectInactiveAsync"/>): the request is read as
    /// one with no session, so a recovery session it also carries still enrols.
    /// </para>
    /// </remarks>
    private async Task<RegistrationSubject?> ResolveRegistrationSubjectAsync(CancellationToken ct)
    {
        if (HttpContext.GetAuthContext() is { IsAuthenticated: true, SubjectId: { } signedIn }
            && !await HttpContext.IsCallerSubjectInactiveAsync(ct))
            return new RegistrationSubject(signedIn, SignedIn: true);

        return await _recoverySession.ReadSubjectAsync(HttpContext, ct) is { } recovering
            ? new RegistrationSubject(recovering, SignedIn: false)
            : null;
    }

    private readonly record struct RegistrationSubject(Guid SubjectId, bool SignedIn);

    /// <summary>
    /// Complete passkey registration with attestation response. The challenge must have been issued for the caller's own account. Under a recovery session this is the session's one enrolment: it issues no session, and the caller then signs in with the new passkey.
    /// </summary>
    /// <remarks>
    /// The challenge must have been issued by <see cref="RegisterOptions"/> for the subject the
    /// caller's credentials resolve to (<see cref="PasskeyRegistrationFlow.OwnAccount"/>). Under a
    /// recovery session the enrolment spends it (<see cref="RecoverySession.EnrolAsync{T}"/>); a
    /// refused attestation leaves it usable. <see cref="PasskeyRegisterCompleteResponse.CredentialId"/>
    /// names the passkey just added, so the caller can offer to remove the older ones.
    /// <para>
    /// Invalidates <see cref="ListCredentials"/> so a caller holding a session sees the new passkey
    /// in the response. A recovery session reaches this endpoint too and cannot read that list; the
    /// generated command settles a refresh that fails and logs it, so the 401 does not fail an
    /// enrolment that succeeded, and a query is returned only to a client that asked for it.
    /// </para>
    /// </remarks>
    [HttpPost("register/complete")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(typeof(PasskeyRegisterCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PasskeyRegisterCompleteResponse>> RegisterComplete(
        [FromBody] PasskeyRegisterCompleteRequest request)
    {
        var ct = HttpContext.RequestAborted;
        if (await ResolveRegistrationSubjectAsync(ct) is not { } registering)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        if (string.IsNullOrEmpty(request.ChallengeToken))
        {
            return Problem(detail: "Challenge token not found or expired", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        PasskeyCredentialResult result;
        try
        {
            if (registering.SignedIn)
            {
                result = await _passkeyService.CompleteRegistrationAsync(
                    request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, tenantId,
                    expectedSubjectId: registering.SubjectId, request.Label);
                await _recoverySession.EndAsync(HttpContext, registering.SubjectId, ct);
            }
            else
            {
                var enrolment = await _recoverySession.EnrolAsync(
                    HttpContext,
                    (sessionSubjectId, _) => _passkeyService.CompleteRegistrationAsync(
                        request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, tenantId,
                        expectedSubjectId: sessionSubjectId, request.Label),
                    _ => true,
                    ct);
                if (enrolment.State != RecoveryEnrolmentState.Enrolled)
                    return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
                result = enrolment.Result!;
            }
        }
        catch (Exception ex) when (IsRegistrationRefusal(ex))
        {
            _logger.LogWarning(ex, "Passkey registration completion failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");
        }

        return Ok(new PasskeyRegisterCompleteResponse
        {
            CredentialId = result.CredentialId,
            SubjectId = result.SubjectId,
        });
    }

    /// <summary>
    /// Asks for a recovery link to be emailed to an address. The answer is the same whether or not the address belongs to anyone: a link is sent to each active member of this site whose confirmed address it is, and starts a recovery session for one enrolment. 404 when this instance has no way to send email.
    /// </summary>
    /// <remarks>
    /// The lookup, the code and the delivery run after the response
    /// (<see cref="RecoveryLinkRequests"/>), so neither the answer nor its timing tells a caller
    /// whether an address is known, confirmed, or was sent anything. The flow exists only while a
    /// delivery route does (<see cref="IAccountLinkRoute.IsConfigured"/>), hence the 404.
    /// </remarks>
    [HttpPost("recovery-link")]
    [AllowAnonymous]
    [EnableRateLimiting("recovery-link")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult RequestRecoveryLink(
        [FromBody] RecoveryLinkRequest request,
        [FromServices] IAccountLinkDelivery delivery,
        [FromServices] RecoveryLinkRequests requests)
    {
        if (!delivery.IsConfigured)
        {
            return this.ProblemWithCode(
                AccountLinkProblemCodes.Unavailable, "Recovery by email is not available.", StatusCodes.Status404NotFound);
        }

        if (HttpContext.GetTenantContext() is { } tenant && !string.IsNullOrWhiteSpace(request.Email))
        {
            _ = requests.RequestAsync(
                tenant,
                request.Email.Trim(),
                AuthAuditActor.From(HttpContext.GetAuthContext()),
                HttpContext.GetClientAddress(),
                Request.Headers.UserAgent.ToString());
        }

        return NoContent();
    }

    /// <summary>
    /// Spends a recovery code an administrator or the operator issued for a member, or a recovery link emailed to the member, and starts the same recovery session <see cref="RecoveryVerify"/> does: one enrolment, a passkey or an identity-provider link, nothing else. A code is single-use. Unknown, spent, expired and other-tenant codes all fail alike.
    /// </summary>
    /// <remarks>
    /// The code is the whole proof. It is single-use and bound to one subject on one tenant. A code
    /// of <see cref="LoginCodeService.RecoveryPurposes"/> is minted only by someone with authority
    /// over that member: a member administrator
    /// (<see cref="V4.Identity.MemberRecoveryController.IssueRecoveryCode"/>) or the operator
    /// (<see cref="V4.PlatformAdmin.TenantController.IssueRecoveryCode"/>), which is how a tenant
    /// whose only owner is locked out gets back in. It is a bearer link that outlives the moment it
    /// was minted, so the member must still be one its issuer may recover
    /// (<see cref="MemberRecoveryEligibility.AllowsRedemption"/> for an administrator's code,
    /// <see cref="MemberRecoveryEligibility.AdmitsOperatorRecoveryAsync"/> for the operator's). An emailed link
    /// (<see cref="Nocturne.Infrastructure.Data.Entities.LoginCodePurpose.EmailRecovery"/>) proves
    /// only control of an address, so it admits a member who can still sign in, but only while the
    /// member still holds that address, confirmed
    /// (<see cref="ISubjectRepository.FindMembersByConfirmedEmailAsync"/>). Unknown, spent,
    /// expired, other-tenant, login and no-longer-applicable codes fail alike.
    /// </remarks>
    [HttpPost("recovery-mode/redeem")]
    [AllowAnonymous]
    [EnableRateLimiting("login-handoff")]
    [RemoteCommand]
    [ProducesResponseType(typeof(RecoveryModeRedeemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecoveryModeRedeemResponse>> RecoveryModeRedeem(
        [FromBody] RecoveryModeRedeemRequest request,
        [FromServices] ILoginCodeService loginCodeService,
        [FromServices] MemberRecoveryEligibility eligibility)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Problem(detail: "A recovery code is required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;
        await using var db = await _dbContextFactory.CreateAsync();
        SubjectRecord? subject = null;
        string? session = null;
        var redeemed = await loginCodeService.RedeemAsync(
            db, request.Code.Trim(), LoginCodeService.SessionStartingPurposes,
            admits: async (code, ct) =>
            {
                var member = await _subjects.GetAsync(code.SubjectId, ct);
                var admitted = code.Purpose switch
                {
                    Nocturne.Infrastructure.Data.Entities.LoginCodePurpose.EmailRecovery =>
                        code.Email is { } email
                        && (await _subjects.FindMembersByConfirmedEmailAsync(tenantId, email, _time.GetUtcNow().UtcDateTime, ct)).Any(m => m.Id == code.SubjectId),
                    Nocturne.Infrastructure.Data.Entities.LoginCodePurpose.OperatorRecovery =>
                        await eligibility.AdmitsOperatorRecoveryAsync(tenantId, member, ct),
                    _ => MemberRecoveryEligibility.AllowsRedemption(
                        await eligibility.CheckAsync(db, tenantId, code.SubjectId, ct)),
                };
                subject = admitted ? member : null;
                return subject is not null;
            },
            // The session is started inside the claim, so a session that cannot start leaves the code
            // unspent. Whoever held the member's own recovery codes, the member's way back in is now
            // this enrolment, so codes they may have lost stop working.
            withSpend: async (code, ct) =>
            {
                session = await _recoverySession.IssueAsync(HttpContext, code.SubjectId, ct);
                await _recoveryCodeService.InvalidateAllAsync(code.SubjectId);
            },
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);

        if (redeemed is null || subject is null || session is null)
        {
            return Problem(
                detail: "This recovery code has expired or was already used.",
                statusCode: 400,
                title: "Bad Request");
        }

        _recoverySession.Present(HttpContext, session);

        return Ok(new RecoveryModeRedeemResponse { Username = subject.Username ?? subject.Name });
    }

    /// <summary>
    /// Returns the subject the registration challenge <paramref name="challengeToken"/> was issued
    /// for by <paramref name="flow"/>'s options step while it is still what that step left for
    /// <paramref name="match"/>
    /// (<see cref="ISubjectRepository.IsEnrollingSubjectAsync"/>) with no membership in any tenant,
    /// or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Each options step creates its own subject and never resumes one, so the challenge it returned
    /// is the only thing that names that subject.
    /// <para>
    /// A subject holding membership anywhere is somebody's account, not an anonymous enrolment.
    /// Subjects are global and passkeys are stored against the subject, so the membership check is
    /// not scoped to the current tenant. It is asked through <see cref="ITenantMemberService"/>,
    /// whose enumeration is pinned to that subject (<c>app.current_subject_id</c>) and so reads only
    /// that subject's memberships.
    /// </para>
    /// </remarks>
    private async Task<Guid?> ResolveEnrollingSubjectAsync(
        string challengeToken, PasskeyRegistrationFlow flow, EnrolmentMatch match)
    {
        if (_passkeyService.FindRegistrationChallengeSubject(challengeToken, flow) is not { } subjectId
            || !await _subjects.IsEnrollingSubjectAsync(subjectId, match, HttpContext.RequestAborted))
        {
            return null;
        }

        var memberships = await _tenantMemberService.GetTenantIdsForSubjectAsync(subjectId);
        return memberships.Count == 0 ? subjectId : null;
    }

    /// <summary>
    /// Generate discoverable assertion options (no username required)
    /// </summary>
    [HttpPost("login/discoverable/options")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-login")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> DiscoverableLoginOptions()
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        var tenantId = _tenantAccessor.TenantId;
        var result = await _passkeyService.GenerateDiscoverableAssertionOptionsAsync(tenantId);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Generate assertion options for a specific user
    /// </summary>
    [HttpPost("login/options")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-login")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> LoginOptions([FromBody] PasskeyLoginOptionsRequest request)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        if (string.IsNullOrEmpty(request.Username))
        {
            return Problem(detail: "Username is required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;
        var result = await _passkeyService.GenerateAssertionOptionsAsync(request.Username, tenantId);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey login with assertion response. A member who has never held recovery codes
    /// gets them in the response when the caller shows them
    /// (<see cref="PasskeyLoginCompleteRequest.ShowsRecoveryCodes"/>,
    /// <see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>).
    /// </summary>
    [HttpPost("login/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-login")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyLoginCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyLoginCompleteResponse>> LoginComplete(
        [FromBody] PasskeyLoginCompleteRequest request)
    {
        if (string.IsNullOrEmpty(request.ChallengeToken))
        {
            return Problem(detail: "Challenge token not found or expired", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;
        var ip = HttpContext.GetClientAddress();
        var ua = Request.Headers.UserAgent.ToString();

        PasskeyAssertionResult assertionResult;
        try
        {
            assertionResult = await _passkeyService.CompleteAssertionAsync(
                request.AssertionResponseJson, request.ChallengeToken, tenantId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Passkey login completion failed");
            return await RefuseLoginAsync(ex.Message, ip, ua);
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "Passkey login completion failed");
            return await RefuseLoginAsync(ex.Message, ip, ua);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Passkey login completion failed");
            return await RefuseLoginAsync(ex.Message, ip, ua);
        }

        // Member(tenantId): the assertion admitted the holder on this tenant only.
        var outcome = await _signIn.CompleteAsync(new SignInAttempt(
            assertionResult.SubjectId,
            SignInMethod.Passkey,
            new SignInAdmission.Member(tenantId),
            new SecondFactorProof.Primary(Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName]),
            new SessionContext(DeviceDescription: "Passkey", IpAddress: ip, UserAgent: ua),
            RefusalAudit: new RefusalAudit.MethodRow()));

        await HttpContext.ApplySignInAsync(outcome, _oidcOptions);

        return outcome switch
        {
            SignInOutcome.SignedIn signedIn => Ok(new PasskeyLoginCompleteResponse
            {
                Success = true,
                AccessToken = signedIn.Session.AccessToken,
                ExpiresIn = signedIn.Session.ExpiresInSeconds,
                RecoveryCodes = request.ShowsRecoveryCodes
                    ? await _recoveryCodeService.GenerateFirstCodesAsync(assertionResult.SubjectId)
                    : null,
            }),
            SignInOutcome.SecondFactorRequired required => Ok(new PasskeyLoginCompleteResponse
            {
                Success = true,
                TotpRequired = true,
                StepUpToken = required.StepUpToken,
            }),
            SignInOutcome.Refused { Reason: SignInRefusal.NotAMember } =>
                await RefuseLoginAsync(PasskeyService.OwnerNotAMemberMessage, ip, ua),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }

    /// <summary>
    /// The answer and row of a refused assertion. A membership that ends between the assertion
    /// and the sign-in is answered the same as one the assertion found missing.
    /// </summary>
    private async Task<ActionResult<PasskeyLoginCompleteResponse>> RefuseLoginAsync(
        string errorMessage, string? ip, string ua)
    {
        await _auditService.LogAsync(AuthAuditEventType.FailedAuth, targetSubjectId: null, success: false,
            ipAddress: ip,
            userAgent: ua,
            errorMessage: errorMessage,
            detailsJson: JsonSerializer.Serialize(new { method = "passkey" }));

        return Problem(detail: "Passkey authentication failed", statusCode: 400, title: "Bad Request");
    }

    /// <summary>
    /// Verify a recovery code and issue a restricted recovery session
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RecoveryModeRedeem"/>, the session starts after the code is spent, so a
    /// session that cannot start costs one of the member's own codes; they hold several.
    /// </remarks>
    [HttpPost("recovery/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-recovery")]
    [RemoteCommand]
    [ProducesResponseType(typeof(RecoveryVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecoveryVerifyResponse>> RecoveryVerify(
        [FromBody] RecoveryVerifyRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Code))
        {
            return Problem(detail: "Username and recovery code are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        // A deactivated member is looked up as no member, so it is refused as an unknown username
        // is and its code is not spent.
        var subject = await _subjects.FindMemberByUsernameAsync(
            tenantId, request.Username, activeOnly: true, _time.GetUtcNow().UtcDateTime, HttpContext.RequestAborted);

        // Unknown usernames go through the same verification; see IRecoveryCodeService.VerifyAndConsumeAsync.
        var verified = await _recoveryCodeService.VerifyAndConsumeAsync(subject?.Id, request.Code);
        if (!verified)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subject?.Id, success: false,
                ipAddress: HttpContext.GetClientAddress(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { method = "recovery_code" }));
            return Problem(detail: "Invalid username or recovery code", statusCode: 400, title: "Bad Request");
        }

        var subjectId = subject!.Id;

        // A recovery code is what someone who lost their factors uses, so nothing an earlier
        // sign-in left behind may shorten the next one.
        var untrusted = await _trustedDevices.RevokeAllAsync(subjectId, HttpContext.RequestAborted);
        if (untrusted > 0)
        {
            await _auditService.LogAsync(AuthAuditEventType.DeviceTrustRevoked, subjectId, success: true,
                ipAddress: HttpContext.GetClientAddress(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { all = true, count = untrusted, reason = "recovery_code" }));
        }

        // Not a completed login: the recovery session only enrols a passkey, and the sign-in with
        // that passkey writes the success row.
        await _auditService.LogAsync(AuthAuditEventType.Login, subjectId, success: false,
            ipAddress: HttpContext.GetClientAddress(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { method = "recovery_code", passkeyEnrolmentPending = true }));

        await _recoverySession.StartAsync(HttpContext, subjectId, HttpContext.RequestAborted);

        return Ok(new RecoveryVerifyResponse
        {
            Success = true,
            RemainingCodes = await _recoveryCodeService.GetRemainingCountAsync(subjectId),
        });
    }

    /// <summary>
    /// List all passkey credentials for the authenticated user
    /// </summary>
    [HttpGet("credentials")]
    [DenyDemoSubject]
    [RemoteQuery]
    [ProducesResponseType(typeof(PasskeyCredentialListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PasskeyCredentialListResponse>> ListCredentials()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var credentials = await _passkeyService.GetCredentialsAsync(auth.SubjectId.Value);
        var primaryFactorCount = await _subjectService.CountPrimaryAuthFactorsAsync(auth.SubjectId.Value);
        var hasSingleSignInMethod = await _subjectService.HasSingleSignInMethodAsync(auth.SubjectId.Value);

        return Ok(new PasskeyCredentialListResponse
        {
            Credentials = credentials.Select(c => new PasskeyCredentialDto
            {
                Id = c.Id,
                Label = c.Label,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
            }).ToList(),
            PrimaryAuthFactorCount = primaryFactorCount,
            HasSingleSignInMethod = hasSingleSignInMethod,
        });
    }

    /// <summary>
    /// Remove a passkey credential. Cannot remove the last credential if user has no OIDC link.
    /// </summary>
    [HttpDelete("credentials/{id:guid}")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveCredential(Guid id)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        // Symmetric factor-count rule is enforced atomically inside the service inside a
        // serializable transaction to prevent TOCTOU races between concurrent removals.
        var result = await _subjectService.TryRemovePasskeyCredentialAsync(auth.SubjectId.Value, id);
        return result == FactorRemovalResult.Removed ? NoContent() : this.FactorRemovalRefusal(result, "Credential");
    }

    /// <summary>
    /// Regenerate recovery codes for the authenticated user. Invalidates all existing codes.
    /// </summary>
    [HttpPost("recovery/regenerate")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetRecoveryStatus", "ListCredentials"])]
    [ProducesResponseType(typeof(RecoveryRegenerateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RecoveryRegenerateResponse>> RegenerateRecoveryCodes()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        if (await HttpContext.IsCallerSubjectInactiveAsync(HttpContext.RequestAborted))
            return Challenge();

        var codes = await _recoveryCodeService.GenerateCodesAsync(auth.SubjectId.Value);

        return Ok(new RecoveryRegenerateResponse
        {
            Codes = codes,
        });
    }

    /// <summary>
    /// Get the count of remaining recovery codes for the authenticated user
    /// </summary>
    [HttpGet("recovery/status")]
    [DenyDemoSubject]
    [RemoteQuery]
    [ProducesResponseType(typeof(RecoveryStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RecoveryStatusResponse>> GetRecoveryStatus()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var remaining = await _recoveryCodeService.GetRemainingCountAsync(auth.SubjectId.Value);
        var hasCodes = await _recoveryCodeService.HasCodesAsync(auth.SubjectId.Value);
        var codesReset = await _recoveryCodeService.WereCodesResetAsync(auth.SubjectId.Value);

        return Ok(new RecoveryStatusResponse
        {
            RemainingCodes = remaining,
            HasCodes = hasCodes,
            TotalCodes = 8,
            CodesReset = codesReset,
        });
    }

    /// <summary>
    /// Returns tenant auth status: whether setup is required, whether recovery mode is active, and whether recovery by email is offered.
    /// </summary>
    /// <remarks>Both states are the setup gate's (<see cref="ITenantMembershipRepository.ReadSetupGateAsync"/>).</remarks>
    [HttpGet("status")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(AuthStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthStatus([FromServices] IAccountLinkDelivery delivery)
    {
        var tenantId = _tenantAccessor.TenantId;

        // System-pinned read: this action is anonymous and reachable on a share host.
        var gate = await _members.ReadSetupGateAsync(tenantId, _time.GetUtcNow().UtcDateTime, HttpContext.RequestAborted);

        var tenant = await _tenants.GetAsync(tenantId, HttpContext.RequestAborted);

        return Ok(new AuthStatusResponse
        {
            SetupRequired = gate is not { HasCredentials: true },
            RecoveryMode = gate?.RecoveryMode == true,
            AllowAccessRequests = tenant?.AllowAccessRequests ?? false,
            OnboardingCompleted = tenant?.OnboardingCompletedAt != null,
            EmailRecoveryOffered = delivery.IsConfigured,
        });
    }

    /// <summary>
    /// Mark the current tenant's onboarding core as complete, enrolling it in the setup hub.
    /// </summary>
    [HttpPost("onboarding/complete")]
    [Authorize]
    [RequireScope(Scope.TenantSettings)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetAuthStatus"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CompleteOnboarding()
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenants.GetAsync(tenantId, HttpContext.RequestAborted);
        if (tenant == null)
            return NotFound();

        if (tenant.OnboardingCompletedAt == null)
        {
            var now = DateTime.UtcNow;
            await _tenants.UpdateAsync(
                tenantId, new TenantEdit(OnboardingCompletedAt: now, SetupHubEnrolledAt: now), HttpContext.RequestAborted);
        }

        return NoContent();
    }

    /// <summary>
    /// Begin passkey registration for an anonymous access request.
    /// Creates a pending subject and returns WebAuthn registration options.
    /// Only available when <c>AllowAccessRequests</c> is enabled on the default tenant.
    /// </summary>
    /// <param name="request">The requestor's display name and optional message.</param>
    /// <returns>A <see cref="PasskeyOptionsResponse"/> with the WebAuthn options and challenge token, or <c>404</c> if access requests are disabled.</returns>
    [HttpPost("access-request/options")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-access-request")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PasskeyOptionsResponse>> AccessRequestOptions(
        [FromBody] AccessRequestOptionsRequest request)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenants.GetAsync(tenantId, HttpContext.RequestAborted);

        if (tenant == null || !tenant.AllowAccessRequests)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Display name is required", statusCode: 400, title: "Bad Request");

        var displayName = request.DisplayName.Trim();
        var username = displayName.ToLowerInvariant().Replace(" ", "-");

        if (await _subjects.AnyPendingAccessRequestNamedAsync(tenantId, displayName, HttpContext.RequestAborted))
            return Conflict(new ProblemDetails
            {
                Detail = "A pending access request with this name already exists",
                Status = 409,
                Title = "Conflict",
            });

        var subjectId = await _subjects.CreateAsync(
            new NewSubject(
                displayName,
                Username: username,
                IsActive: false,
                ApprovalStatus: nameof(ApprovalStatus.Pending),
                AccessRequestMessage: request.Message?.Trim()),
            HttpContext.RequestAborted);

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(subjectId, username, PasskeyRegistrationFlow.AccessRequest);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration for an anonymous access request: stores the credential and
    /// files a membership request for the pending subject on the tenant this host resolved to, which
    /// the owners see and decide (<see cref="IMembershipRequestService"/>). The subject remains
    /// inactive until one approves.
    /// </summary>
    /// <remarks>
    /// The challenge must have been minted by <see cref="AccessRequestOptions"/>
    /// (<see cref="PasskeyRegistrationFlow.AccessRequest"/>), and the subject it names must still be
    /// inactive and pending under this display name, with no way to sign in and no membership in
    /// any tenant. Any other challenge is refused.
    /// <para>
    /// The request is filed in a scope that reads as no one on this tenant: the ceremony is anonymous
    /// and may run on a share host, whose reader sees neither members nor requests, so read as the
    /// caller it could not find the members to notify nor a request already filed. It returns only
    /// the outcome.
    /// </para>
    /// <para>
    /// The credential and the request stand or fall together. A credential whose request was not
    /// stored is removed, so the visitor can run the ceremony again under the same name. Once the
    /// credential is stored the filing ignores the visitor's disconnect, and a stored request keeps
    /// its credential whatever fails after it: an owner who approves it must leave a member who can
    /// sign in. A request an earlier attempt filed counts as filed.
    /// </para>
    /// </remarks>
    /// <param name="request">The display name, attestation response, and challenge token from the WebAuthn ceremony.</param>
    /// <param name="scopes">Opens the scope the membership request is filed in.</param>
    /// <param name="passkeys">Removes the credential when the request could not be filed.</param>
    /// <returns><c>200 OK</c> on success, or <c>400</c> / <c>404</c> on error.</returns>
    [HttpPost("access-request/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-access-request")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AccessRequestComplete(
        [FromBody] AccessRequestCompleteRequest request,
        [FromServices] IServiceScopeFactory scopes,
        [FromServices] IPasskeyCredentialRepository passkeys)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenants.GetAsync(tenantId, HttpContext.RequestAborted);

        if (tenant == null || !tenant.AllowAccessRequests || _tenantAccessor.Context is not { } tenantContext)
            return NotFound();

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName))
            return Problem(detail: "Start the access request again", statusCode: 400, title: "Bad Request");

        var enrollingSubjectId = await ResolveEnrollingSubjectAsync(
            request.ChallengeToken, PasskeyRegistrationFlow.AccessRequest, new EnrolmentMatch.AccessRequest(displayName));
        if (enrollingSubjectId == null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");

        var credResult = await TryCompleteRegistrationAsync(
            request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.AccessRequest,
            tenant.Id, enrollingSubjectId.Value);
        if (credResult is null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");

        await using var tenantScope = TenantJobScope.Open(scopes, tenantContext, AccessRequestActor);
        var requests = tenantScope.ServiceProvider.GetRequiredService<IMembershipRequestService>();

        CreateMembershipRequestResult? result = null;
        try
        {
            var message = (await _subjects.GetAsync(credResult.SubjectId, CancellationToken.None))?.AccessRequestMessage;
            result = await requests.CreateRequestAsync(tenant.Id, credResult.SubjectId, message, CancellationToken.None);
            return IsFiled(result)
                ? Ok()
                : Problem(detail: result.Error, statusCode: 400, title: "Bad Request");
        }
        finally
        {
            if (!IsFiled(result) && (result is not null || !await HasPendingRequestAsync(requests, tenant.Id, credResult.SubjectId)))
                await passkeys.RemoveAsync(credResult.CredentialId, credResult.SubjectId, CancellationToken.None);
        }
    }

    private static bool IsFiled(CreateMembershipRequestResult? result) =>
        result?.Outcome is CreateMembershipRequestOutcome.Created or CreateMembershipRequestOutcome.AlreadyPending;

    private static async Task<bool> HasPendingRequestAsync(IMembershipRequestService requests, Guid tenantId, Guid subjectId) =>
        await requests.GetMyRequestAsync(tenantId, subjectId, CancellationToken.None) is { Status: MembershipRequestRecord.Pending };

    /// <summary>
    /// Generate passkey registration options for an unauthenticated user accepting an invite.
    /// Validates the invite, creates a new subject, and returns WebAuthn registration options.
    /// </summary>
    [HttpPost("invite/options")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> InviteOptions(
        [FromBody] InviteOptionsRequest request,
        [FromServices] IMemberInviteService memberInviteService)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(detail: "Token, username, and display name are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        // Validate the invite against the tenant this request resolved to; a token minted for
        // another tenant must not mint a subject here.
        var invite = await memberInviteService.GetInviteByTokenAsync(request.Token, tenantId);
        if (invite == null || !invite.IsValid)
            return NotFound();

        var username = request.Username.Trim().ToLowerInvariant();
        var displayName = request.DisplayName.Trim();

        var subjectId = await _subjects.CreateAsync(
            new NewSubject(displayName, Username: username), HttpContext.RequestAborted);

        _logger.LogInformation(
            "Invite: enrolling subject {SubjectId} for invite acceptance",
            subjectId);

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(subjectId, username, PasskeyRegistrationFlow.MemberInvite);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration for an invite acceptance.
    /// Verifies attestation, accepts the invite, completes the sign-in, then generates recovery codes
    /// (<see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>) before the session cookies are set.
    /// </summary>
    /// <remarks>
    /// The challenge must have been minted by <see cref="InviteOptions"/>
    /// (<see cref="PasskeyRegistrationFlow.MemberInvite"/>), and the subject it names must still be
    /// active and approved under this username, with no way to sign in and no membership in any
    /// tenant. Any other challenge is refused.
    /// <para>
    /// The sign-in checks the membership the acceptance made. The invitee holds no authenticator,
    /// so it owes no code; were one owed, the answer carries the recovery codes and no session, and
    /// the invitee signs in with the passkey and the code. Once the invite is accepted, a failure
    /// leaves a member who signs in with the passkey on the login page.
    /// </para>
    /// </remarks>
    [HttpPost("invite/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyRegistrationResponse>> InviteComplete(
        [FromBody] InviteCompleteRequest request,
        [FromServices] IMemberInviteService memberInviteService)
    {
        if (string.IsNullOrEmpty(request.ChallengeToken) || string.IsNullOrEmpty(request.Token)
            || string.IsNullOrWhiteSpace(request.Username))
        {
            return Problem(detail: "Username, challenge token, and invite token are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        var username = request.Username.Trim().ToLowerInvariant();
        var enrollingSubjectId = await ResolveEnrollingSubjectAsync(
            request.ChallengeToken, PasskeyRegistrationFlow.MemberInvite, new EnrolmentMatch.Invite(username));

        if (enrollingSubjectId == null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");

        var credResult = await TryCompleteRegistrationAsync(
            request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.MemberInvite,
            tenantId, enrollingSubjectId.Value);
        if (credResult is null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");

        var acceptResult = await memberInviteService.AcceptInviteAsync(
            request.Token, credResult.SubjectId, tenantId);
        if (!acceptResult.Success)
        {
            // A refused invite joins no one, so the passkey would leave a subject that signs in
            // nowhere.
            await _passkeyService.RemoveCredentialAsync(credResult.CredentialId, credResult.SubjectId, tenantId);
            return Problem(detail: acceptResult.ErrorDescription ?? "Failed to accept invite", statusCode: 400, title: "Invite Error");
        }

        var outcome = await _signIn.CompleteAsync(new SignInAttempt(
            credResult.SubjectId,
            SignInMethod.Passkey,
            new SignInAdmission.Member(tenantId),
            new SecondFactorProof.Primary(Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName]),
            new SessionContext(
                DeviceDescription: "Invite Passkey",
                IpAddress: HttpContext.GetClientAddress(),
                UserAgent: Request.Headers.UserAgent.ToString()),
            Enrolment: SignInEnrolment.Invite));

        List<string> recoveryCodes = outcome is SignInOutcome.Refused
            ? []
            : await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);

        await HttpContext.ApplySignInAsync(outcome, _oidcOptions);

        switch (outcome)
        {
            case SignInOutcome.SignedIn signedIn:
                _logger.LogInformation(
                    "Invite complete: subject {SubjectId} registered with passkey via invite",
                    credResult.SubjectId);
                return Ok(new PasskeyRegistrationResponse
                {
                    Success = true,
                    RecoveryCodes = recoveryCodes,
                    AccessToken = signedIn.Session.AccessToken,
                    RefreshToken = signedIn.Session.RefreshToken,
                    ExpiresIn = signedIn.Session.ExpiresInSeconds,
                });
            case SignInOutcome.SecondFactorRequired:
                return Ok(new PasskeyRegistrationResponse { Success = true, RecoveryCodes = recoveryCodes });
            case SignInOutcome.Refused { Reason: SignInRefusal.NotAMember }:
                return Problem(
                    detail: "The invite was accepted, but the account is no longer a member.",
                    statusCode: 400, title: "Invite Error");
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>
    /// The registration proof of an enrolment flow: the stored credential, or null when the
    /// attestation is refused.
    /// </summary>
    /// <remarks>
    /// Refusals are those <see cref="IsRegistrationRefusal"/> names. Anything else is not a refusal
    /// and is left to the error handler.
    /// </remarks>
    private async Task<PasskeyCredentialResult?> TryCompleteRegistrationAsync(
        string attestationResponseJson, string challengeToken, PasskeyRegistrationFlow flow, Guid tenantId,
        Guid enrollingSubjectId)
    {
        try
        {
            return await _passkeyService.CompleteRegistrationAsync(
                attestationResponseJson, challengeToken, flow, tenantId, expectedSubjectId: enrollingSubjectId);
        }
        catch (Exception ex) when (IsRegistrationRefusal(ex))
        {
            _logger.LogWarning(ex, "Passkey registration for {Flow} was refused", flow);
            return null;
        }
    }

    /// <summary>
    /// The refusals <see cref="IPasskeyService.CompleteRegistrationAsync"/> names, and so also
    /// anything of those types the store raises, which rolls back and leaves only the spent
    /// challenge.
    /// </summary>
    private static bool IsRegistrationRefusal(Exception ex) =>
        ex is InvalidOperationException or JsonException or PasskeyRegistrationRefusedException;

    /// <summary>
    /// Generate passkey registration options for someone without an account who holds a tenant
    /// creation invite. Checks the link, the subdomain and the username before creating the
    /// subject the ceremony enrols; nothing is claimed until the complete step.
    /// </summary>
    [HttpPost("tenant-invite/options")]
    [AllowAnonymous]
    [DenyDemoSubject]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TenantInviteOptions(
        [FromBody] TenantInviteOptionsRequest request,
        [FromServices] ITenantCreationInviteService tenantInvites)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.SiteName))
            return Problem(detail: "Your name and the site name are required", statusCode: 400, title: "Bad Request");

        var username = UsernameRules.Normalize(request.Username ?? "");
        if (UsernameRules.Refusal(username) is { } usernameRefusal)
            return Problem(detail: usernameRefusal, statusCode: 400, title: "Bad Request");

        var info = await tenantInvites.GetInfoAsync(request.Token, HttpContext.RequestAborted);
        if (info.Refusal is { } inviteRefusal)
            return this.ToResult(inviteRefusal);

        var slugCheck = await _tenantService.ValidateSlugAsync(request.Slug, HttpContext.RequestAborted);
        if (!slugCheck.IsValid)
            return Problem(detail: slugCheck.Message, statusCode: 400, title: "Bad Request");

        var displayName = request.DisplayName.Trim();

        var subjectId = await _subjects.CreateAsync(
            new NewSubject(displayName, Username: username), HttpContext.RequestAborted);

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(subjectId, username, PasskeyRegistrationFlow.CreationLink);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration for a tenant creation invite: takes one use of the link,
    /// verifies the attestation, creates the tenant with the new subject as its owner, completes the
    /// sign-in, then generates recovery codes (<see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>)
    /// before the session cookies are set.
    /// </summary>
    /// <remarks>
    /// The use is taken before the ceremony and given back if it fails, so two people on a
    /// single-use link cannot both finish. A tenant that cannot be created after the passkey is
    /// stored leaves an account with no tenant, so the response still signs it in and names the
    /// refusal: the page then redeems the link again for the signed-in account through
    /// <c>TenantCreationInviteController.Redeem</c>. Passkey sign-in needs a tenant, so this
    /// session is that account's way in.
    /// <para>
    /// The challenge must have been minted by <see cref="TenantInviteOptions"/>
    /// (<see cref="PasskeyRegistrationFlow.CreationLink"/>), which checked the username, and the
    /// subject it names must still be active and approved under this username, with no way to sign
    /// in and no membership in any tenant. Any other challenge is refused.
    /// </para>
    /// <para>
    /// That subject holds no authenticator, so the sign-in owes no code. Were one owed, the answer
    /// carries the recovery codes and no session: the owner signs in with the passkey and the code.
    /// </para>
    /// </remarks>
    [HttpPost("tenant-invite/complete")]
    [AllowAnonymous]
    [DenyDemoSubject]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TenantInviteRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TenantInviteComplete(
        [FromBody] TenantInviteCompleteRequest request,
        [FromServices] ITenantCreationInviteService tenantInvites)
    {
        if (string.IsNullOrEmpty(request.ChallengeToken) || string.IsNullOrWhiteSpace(request.Username))
            return Problem(detail: "Username and challenge token are required", statusCode: 400, title: "Bad Request");

        var username = UsernameRules.Normalize(request.Username);
        var enrollingSubjectId = await ResolveEnrollingSubjectAsync(
            request.ChallengeToken, PasskeyRegistrationFlow.CreationLink, new EnrolmentMatch.Invite(username));
        if (enrollingSubjectId == null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");

        var slugCheck = await _tenantService.ValidateSlugAsync(request.Slug, HttpContext.RequestAborted);
        if (!slugCheck.IsValid)
            return Problem(detail: slugCheck.Message, statusCode: 400, title: "Bad Request");

        var claim = await tenantInvites.ClaimAsync(request.Token, HttpContext.RequestAborted);
        if (claim.Refusal is { } claimRefusal)
            return this.ToResult(claimRefusal);

        PasskeyCredentialResult? credResult = null;
        try
        {
            credResult = await TryCompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.CreationLink,
                Guid.Empty, enrollingSubjectId.Value);
        }
        finally
        {
            if (credResult is null)
                await tenantInvites.ReleaseAsync(claim.Value);
        }

        if (credResult is null)
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");

        // The passkey is stored, so from here the account exists whatever happens to the tenant:
        // a failure is reported beside the session rather than in place of it.
        TenantInviteOutcome<TenantCreationInviteRedeemed> created;
        try
        {
            created = await tenantInvites.CreateTenantAsync(
                claim.Value, request.Slug, request.SiteName, credResult.SubjectId, username, CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Tenant invite: the passkey was stored but the tenant could not be created");
            created = TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(
                TenantInviteRefusalCode.InvalidRequest, "We could not make your site.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Tenant invite: the passkey was stored but the tenant could not be created");
            created = TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(
                TenantInviteRefusalCode.InvalidRequest, "We could not make your site.");
        }

        // The owner membership is made with the tenant, so a created tenant is checked for it. A
        // refused tenant leaves an account that is a member nowhere, so there is none to check.
        SignInAdmission admission = created.Value is { } tenant
            ? new SignInAdmission.Member(tenant.TenantId)
            : new SignInAdmission.Account();
        var outcome = await _signIn.CompleteAsync(new SignInAttempt(
            credResult.SubjectId,
            SignInMethod.Passkey,
            admission,
            new SecondFactorProof.Primary(Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName]),
            new SessionContext(
                DeviceDescription: "Invite Passkey",
                IpAddress: HttpContext.GetClientAddress(),
                UserAgent: Request.Headers.UserAgent.ToString()),
            Enrolment: SignInEnrolment.TenantInvite));

        List<string> recoveryCodes = outcome is SignInOutcome.Refused
            ? []
            : await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);

        await HttpContext.ApplySignInAsync(outcome, _oidcOptions);

        // The account exists and is signed in either way, so a refused tenant is reported beside
        // the session rather than in place of it: the page shows the recovery codes, then lets the
        // new owner pick another subdomain on the signed-in path.
        return outcome switch
        {
            SignInOutcome.SignedIn signedIn => Ok(new TenantInviteRegistrationResponse
            {
                Success = true,
                RecoveryCodes = recoveryCodes,
                AccessToken = signedIn.Session.AccessToken,
                RefreshToken = signedIn.Session.RefreshToken,
                ExpiresIn = signedIn.Session.ExpiresInSeconds,
                Tenant = created.Value,
                TenantRefusal = created.Refusal?.Message,
            }),
            SignInOutcome.SecondFactorRequired => Ok(new TenantInviteRegistrationResponse
            {
                Success = true,
                RecoveryCodes = recoveryCodes,
                Tenant = created.Value,
                TenantRefusal = created.Refusal?.Message,
            }),
            SignInOutcome.Refused { Reason: SignInRefusal.NotAMember } =>
                Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }
}

#region Request/Response DTOs

/// <summary>
/// Response containing WebAuthn options and the encrypted challenge token
/// </summary>
public class PasskeyOptionsResponse
{
    public string Options { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

/// <summary>
/// Request for passkey registration options. There is deliberately no subject id here — the
/// server resolves the subject from the caller's credentials, because a caller-supplied one is an
/// anonymous account-takeover.
/// </summary>
public class PasskeyRegisterOptionsRequest
{
    /// <remarks>
    /// Only the label the authenticator shows for the credential.
    /// </remarks>
    public string Username { get; set; } = string.Empty;
}

/// <summary>The address a recovery link is asked for.</summary>
public class RecoveryLinkRequest
{
    public string? Email { get; set; }
}

/// <summary>
/// A recovery code an administrator or the operator issued for a member, or one from an emailed
/// recovery link.
/// </summary>
public class RecoveryModeRedeemRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// A spent recovery-mode code. The account it names can now enrol one passkey or link one identity provider.
/// </summary>
public class RecoveryModeRedeemResponse
{
    /// <summary>The account the code was issued for, for labelling the new passkey.</summary>
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete passkey registration
/// </summary>
public class PasskeyRegisterCompleteRequest
{
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
    public string? Label { get; set; }
}

/// <summary>
/// Response for completed passkey registration
/// </summary>
public class PasskeyRegisterCompleteResponse
{
    public Guid CredentialId { get; set; }
    public Guid SubjectId { get; set; }
}

/// <summary>
/// Request for passkey login options
/// </summary>
public class PasskeyLoginOptionsRequest
{
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete passkey login
/// </summary>
public class PasskeyLoginCompleteRequest
{
    public string AssertionResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether the caller shows the recovery codes of its <see cref="PasskeyLoginCompleteResponse"/> to the person.
    /// Only such a caller is given first codes: codes it dropped would be stored and never seen.
    /// </summary>
    public bool ShowsRecoveryCodes { get; set; }
}

/// <summary>
/// Response for completed passkey login. When <see cref="TotpRequired"/> is set the passkey was
/// accepted but no session exists yet: the caller must post <see cref="StepUpToken"/> with an
/// authenticator code to <c>/api/auth/totp/login</c>.
/// </summary>
public class PasskeyLoginCompleteResponse
{
    public bool Success { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public bool TotpRequired { get; set; }
    public string? StepUpToken { get; set; }

    /// <summary>
    /// The member's first recovery codes, when this sign-in issued them
    /// (<see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>). They are shown nowhere else.
    /// </summary>
    public List<string>? RecoveryCodes { get; set; }
}

/// <summary>
/// Request to verify a recovery code
/// </summary>
public class RecoveryVerifyRequest
{
    public string Username { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Response for recovery code verification
/// </summary>
public class RecoveryVerifyResponse
{
    public bool Success { get; set; }
    public int RemainingCodes { get; set; }
}

/// <summary>
/// Response containing the list of passkey credentials
/// </summary>
public class PasskeyCredentialListResponse
{
    public List<PasskeyCredentialDto> Credentials { get; set; } = new();
    public int PrimaryAuthFactorCount { get; set; }

    /// <summary>
    /// True when this account can be signed into exactly one way, with no unused recovery codes
    /// behind it.
    /// </summary>
    public bool HasSingleSignInMethod { get; set; }
}

/// <summary>
/// A passkey credential summary (never includes the public key)
/// </summary>
public class PasskeyCredentialDto
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// Response containing regenerated recovery codes
/// </summary>
public class RecoveryRegenerateResponse
{
    public List<string> Codes { get; set; } = new();
}

/// <summary>
/// Response containing recovery code status
/// </summary>
public class RecoveryStatusResponse
{
    public int RemainingCodes { get; set; }
    public bool HasCodes { get; set; }
    public int TotalCodes { get; set; }

    /// <summary>
    /// True when the subject's codes were invalidated and none have been generated since.
    /// </summary>
    public bool CodesReset { get; set; }
}

/// <summary>
/// Instance auth status
/// </summary>
public class AuthStatusResponse
{
    public bool SetupRequired { get; set; }
    public bool RecoveryMode { get; set; }
    public bool AllowAccessRequests { get; set; }
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// Whether a member can ask for a recovery link by email: this instance can mint and send one
    /// (<see cref="IAccountLinkRoute.IsConfigured"/>).
    /// </summary>
    public bool EmailRecoveryOffered { get; set; }
}

/// <summary>
/// Response for a completed passkey registration that issues a session
/// (recovery codes plus session tokens).
/// </summary>
public class PasskeyRegistrationResponse
{
    public bool Success { get; set; }
    public List<string> RecoveryCodes { get; set; } = new();
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
}

public class AccessRequestOptionsRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>
/// Request to complete an anonymous access request. The challenge token is the one the access
/// request options step returned; it names the subject that step created.
/// </summary>
public class AccessRequestCompleteRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

public class InviteOptionsRequest
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete an invite acceptance. The challenge token is the one the invite options step
/// returned; it names the subject that step created.
/// </summary>
public class InviteCompleteRequest
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

public class TenantInviteOptionsRequest
{
    public string Token { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete a tenant creation invite. The challenge token is the one the tenant invite
/// options step returned; it names the subject that step created.
/// </summary>
public class TenantInviteCompleteRequest
{
    public string Token { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

/// <summary>
/// A completed tenant creation invite registration: the new account's session, and the tenant it
/// owns or why that tenant could not be created.
/// </summary>
public class TenantInviteRegistrationResponse : PasskeyRegistrationResponse
{
    public TenantCreationInviteRedeemed? Tenant { get; set; }

    public string? TenantRefusal { get; set; }
}

#endregion

