using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Controller for WebAuthn/FIDO2 passkey authentication ceremonies.
/// Handles registration, login (both discoverable and non-discoverable), and recovery code verification.
/// </summary>
/// <remarks>
/// Authentication flows:
/// <list type="bullet">
///   <item><description><b>Registration</b> (authenticated, enrols onto the caller's own account): <c>POST /register/options</c> → <c>POST /register/complete</c></description></item>
///   <item><description><b>Discoverable login</b> (no username): <c>POST /login/discoverable/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Non-discoverable login</b> (with username): <c>POST /login/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Recovery:</b> <c>POST /recovery/verify</c> issues a 10-minute restricted token allowing passkey management only.</description></item>
///   <item><description><b>Recovery mode:</b> <c>POST /recovery-mode/redeem</c> spends an administrator-issued code for the same restricted session, for a member with no sign-in method left.</description></item>
///   <item><description><b>Initial setup:</b> handled by the setup controller under <c>/api/v4/setup/</c>, not here.</description></item>
///   <item><description><b>Invite acceptance:</b> <c>POST /invite/options</c> → <c>POST /invite/complete</c> using a pre-issued invite token.</description></item>
/// </list>
///
/// On successful login or setup, the controller uses
/// <see cref="SessionCookieExtensions.SetSessionCookies(HttpResponse, SessionTokenPair, OidcOptions)"/> to set session cookies.
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
[Tags("Authentication")]
[AllowDuringSetup]
public class PasskeyController : ControllerBase
{
    private const string RecoveryCookieName = ".Nocturne.RecoverySession";

    /// <summary>
    /// How long a spent recovery code stays redeemable for one passkey enrolment. Bounds both the
    /// token and the cookie carrying it, so neither outlives the other.
    /// </summary>
    private static readonly TimeSpan RecoverySessionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What makes a token a recovery session, as opposed to any credential that merely carries
    /// <see cref="RecoverySessionPermission"/>. It sits outside
    /// <see cref="Core.Models.Authorization.Scope.ValidRequestScopes"/>, so no client can
    /// register it and no scope gate resolves anything from it, leaving
    /// <see cref="RecoveryVerify"/> its only source.
    /// </summary>
    private const string RecoverySessionScope = "auth:recovery:enrol";

    /// <summary>
    /// The authority a recovery session confers: enrol a replacement passkey, nothing else.
    /// </summary>
    private const string RecoverySessionPermission = "passkey:manage";

    private readonly IPasskeyService _passkeyService;
    private readonly ITotpService _totpService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly IJwtService _jwtService;
    private readonly ISessionService _sessionService;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantService _tenantService;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly INocturneDbContextFactory _dbContextFactory;
    private readonly OidcOptions _oidcOptions;
    private readonly ILogger<PasskeyController> _logger;

    /// <summary>
    /// Creates a new instance of PasskeyController
    /// </summary>
    public PasskeyController(
        IPasskeyService passkeyService,
        ITotpService totpService,
        IRecoveryCodeService recoveryCodeService,
        IJwtService jwtService,
        ISessionService sessionService,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        ITenantAccessor tenantAccessor,
        ITenantService tenantService,
        ITenantMemberService tenantMemberService,
        INocturneDbContextFactory dbContextFactory,
        IOptions<OidcOptions> oidcOptions,
        ILogger<PasskeyController> logger)
    {
        _passkeyService = passkeyService;
        _totpService = totpService;
        _recoveryCodeService = recoveryCodeService;
        _jwtService = jwtService;
        _sessionService = sessionService;
        _subjectService = subjectService;
        _auditService = auditService;
        _tenantAccessor = tenantAccessor;
        _tenantService = tenantService;
        _tenantMemberService = tenantMemberService;
        _dbContextFactory = dbContextFactory;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generate registration options for a new passkey credential on the caller's own account.
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

        var subjectId = ResolveRegistrationSubject();
        if (subjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        if (string.IsNullOrEmpty(request.Username))
        {
            return Problem(detail: "Username is required", statusCode: 400, title: "Bad Request");
        }

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId.Value, request.Username);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Resolves which subject a registration ceremony is allowed to bind a credential to.
    /// </summary>
    /// <returns>The subject to register against, or <see langword="null"/> when none qualifies.</returns>
    /// <remarks>
    /// <para>
    /// The request deliberately gets no say in this. The subject id is sealed into the challenge
    /// token here, so honouring a caller-supplied one let an anonymous caller bind their own
    /// authenticator to any subject whose id they knew and then log in as them.
    /// </para>
    /// <para>
    /// Two flows reach these endpoints: adding a passkey to your own account, and re-registering
    /// after spending a recovery code. The remaining enrolments — first owner, invite acceptance,
    /// access request, and an account with no sign-in method left — each have their own endpoint
    /// with its own precondition, so none of them resolve a subject here.
    /// </para>
    /// </remarks>
    private Guid? ResolveRegistrationSubject()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is { IsAuthenticated: true, SubjectId: not null })
            return auth.SubjectId;

        return TryReadRecoverySessionSubject();
    }

    /// <summary>
    /// Reads the subject out of the short-lived recovery session minted by
    /// <see cref="RecoveryVerify"/>, which is the proof that a recovery code was spent.
    /// </summary>
    private Guid? TryReadRecoverySessionSubject()
    {
        var token = Request.Cookies[RecoveryCookieName];
        if (string.IsNullOrEmpty(token))
            return null;

        var validation = _jwtService.ValidateAccessToken(token);
        if (!validation.IsValid || validation.Claims is null)
            return null;

        return validation.Claims.Scopes.Contains(RecoverySessionScope)
            && validation.Claims.Permissions.Contains(RecoverySessionPermission)
            ? validation.Claims.SubjectId
            : null;
    }

    /// <summary>
    /// The attributes the recovery-session cookie is written with. A cookie is keyed by name,
    /// domain and path, so the write and the expiry that spends it must present the same ones.
    /// Host-only: the recovery session is redeemed on the host it was issued from, so unlike a
    /// session cookie it is never widened to sibling tenants.
    /// </summary>
    private CookieOptions RecoveryCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = _oidcOptions.Cookie.Secure,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    /// <summary>
    /// Sets the short-lived cookie that lets <paramref name="subject"/> enrol one passkey, read
    /// back by <see cref="TryReadRecoverySessionSubject"/>.
    /// </summary>
    private void StartRecoverySession(SubjectEntity subject)
    {
        var recoveryToken = _jwtService.GenerateAccessToken(
            new SubjectInfo { Id = subject.Id, Name = subject.Name, Email = subject.Email },
            permissions: [RecoverySessionPermission],
            roles: [],
            scopes: [RecoverySessionScope],
            lifetime: RecoverySessionLifetime);

        var cookieOptions = RecoveryCookieOptions();
        cookieOptions.MaxAge = RecoverySessionLifetime;
        Response.Cookies.Append(RecoveryCookieName, recoveryToken, cookieOptions);
    }

    /// <summary>
    /// Complete passkey registration with attestation response
    /// </summary>
    /// <remarks>
    /// The challenge must have been issued for the subject the caller's credentials resolve to,
    /// so a challenge minted by another flow cannot be redeemed as an enrolment onto it.
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
        var subjectId = ResolveRegistrationSubject();
        if (subjectId == null)
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
            result = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenantId,
                expectedSubjectId: subjectId.Value, request.Label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passkey registration completion failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");
        }

        // One spent recovery code buys one enrolment: the credential it authorized now exists,
        // so the session that authorized it is over even though its token has time left.
        Response.Cookies.Delete(RecoveryCookieName, RecoveryCookieOptions());

        // The passkey exists whatever happens here, so a failure is logged rather than reported:
        // a code left outstanding is refused at redemption anyway, since the member can sign in.
        try
        {
            await using var db = await _dbContextFactory.CreateAsync();
            await LoginCodeService.VoidRecoveryCodesAsync(db, subjectId.Value, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not void outstanding recovery codes for {SubjectId}", subjectId.Value);
        }

        return Ok(new PasskeyRegisterCompleteResponse
        {
            CredentialId = result.CredentialId,
            SubjectId = result.SubjectId,
        });
    }

    /// <summary>
    /// Spends a recovery code an administrator issued for a member and starts the same restricted
    /// session <see cref="RecoveryVerify"/> does: one passkey enrolment through
    /// <see cref="RegisterOptions"/>, nothing else.
    /// </summary>
    /// <remarks>
    /// The code is the whole proof. It is single-use, bound to one subject on one tenant, minted
    /// for <see cref="LoginCodePurpose.Recovery"/>, and only someone with authority over that
    /// member can mint it: a member administrator
    /// (<see cref="V4.Identity.MemberRecoveryController.IssueRecoveryCode"/>) or the operator
    /// (<see cref="V4.PlatformAdmin.TenantController.IssueRecoveryCode"/>), which is how a tenant
    /// whose only owner is locked out gets back in. The code is a bearer link that outlives the
    /// moment it was minted, so the member must still be one its issuer may recover
    /// (<see cref="MemberRecoveryEligibility.AllowsRedemption"/>). Unknown, spent, expired,
    /// other-tenant, login and no-longer-applicable codes fail alike.
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

        await using var db = await _dbContextFactory.CreateAsync();
        var redeemed = await loginCodeService.RedeemAsync(
            db, request.Code.Trim(), LoginCodeService.RecoveryPurposes,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);

        var subject = redeemed is not null
            && MemberRecoveryEligibility.AllowsRedemption(
                await eligibility.CheckAsync(db, _tenantAccessor.TenantId, redeemed.SubjectId, HttpContext.RequestAborted),
                redeemed.Purpose)
            ? await db.Subjects.AsNoTracking().FirstOrDefaultAsync(s => s.Id == redeemed.SubjectId)
            : null;
        if (subject is null)
        {
            return Problem(
                detail: "This recovery code has expired or was already used.",
                statusCode: 400,
                title: "Bad Request");
        }

        // Whoever held the member's own recovery codes, the member's way back in is now this
        // enrolment, so codes they may have lost stop working.
        await _recoveryCodeService.InvalidateAllAsync(subject.Id);
        StartRecoverySession(subject);

        return Ok(new RecoveryModeRedeemResponse { Username = subject.Username ?? subject.Name });
    }

    /// <summary>
    /// Names the subject an invite acceptance enrols. The options and complete steps share it, so
    /// the subject the options step reuses or creates is the one the complete step re-resolves.
    /// </summary>
    private static Expression<Func<SubjectEntity, bool>> InviteEnrolmentMatch(string username) =>
        s => s.Username == username && s.IsActive && s.ApprovalStatus != "Pending";

    /// <summary>
    /// Names the subject an anonymous access request enrols. Shared by the options and complete
    /// steps for the same reason as <see cref="InviteEnrolmentMatch"/>.
    /// </summary>
    private static Expression<Func<SubjectEntity, bool>> AccessRequestEnrolmentMatch(string displayName) =>
        s => s.Name == displayName && !s.IsActive && s.ApprovalStatus == "Pending";

    /// <summary>
    /// Returns the id of the most recently created subject matching <paramref name="match"/> that is
    /// still part-way through an anonymous enrolment — no passkey, no linked provider, and no
    /// membership in any tenant — or <see langword="null"/> when nothing matches.
    /// </summary>
    /// <remarks>
    /// The invite and access-request options steps create the subject, so the complete step has to
    /// re-resolve it: the enrolling subject must not be taken from the challenge token, and the
    /// invite token is not a key for it because one invite can be accepted by several people.
    /// <para>
    /// A subject holding membership anywhere is somebody's account, not an anonymous enrolment.
    /// Subjects are global and passkeys are stored against the subject, so scoping the membership
    /// check to the current tenant would let a tenant claim another tenant's credential-less member
    /// — the locked-out state recovery mode exists for — by enrolling a passkey onto them.
    /// </para>
    /// <para>
    /// Hence the two steps. The candidate list comes from tables that are not tenant-scoped, so one
    /// query answers it. The membership check is asked per candidate through
    /// <see cref="ITenantMemberService"/>, whose enumeration is pinned to that subject
    /// (<c>app.current_subject_id</c>) and so reads only that subject's memberships.
    /// </para>
    /// The preconditions mean only a half-finished enrolment can match, never an account that can
    /// already sign in or that belongs to a tenant — so with several candidates (duplicates left by
    /// an older build of the options step) every one of them is an empty shell and the newest, which
    /// the caller's ceremony was minted against, wins. Ids are UUID v7, which sort in creation order.
    /// Walking the candidates newest-first and taking the first with no membership is the same
    /// answer as the newest candidate satisfying all four conditions at once.
    /// </remarks>
    private async Task<Guid?> FindEnrollingSubjectIdAsync(NocturneDbContext db, Expression<Func<SubjectEntity, bool>> match)
    {
        var candidateIds = await db.Subjects
            .Where(match)
            .Where(s => !s.IsSystemSubject
                && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
                && !db.WorkingOidcIdentities().Any(o => o.SubjectId == s.Id))
            .OrderByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var candidateId in candidateIds)
        {
            var memberships = await _tenantMemberService.GetTenantIdsForSubjectAsync(candidateId);
            if (memberships.Count == 0)
            {
                return candidateId;
            }
        }

        return null;
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
    /// Complete passkey login with assertion response
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

        try
        {
            var assertionResult = await _passkeyService.CompleteAssertionAsync(
                request.AssertionResponseJson, request.ChallengeToken, tenantId);

            // A subject with an authenticator enrolled finishes signing in at
            // POST /api/auth/totp/login. No session is issued here, so the passkey alone does
            // not grant access.
            if (await _totpService.GetCredentialCountAsync(assertionResult.SubjectId) > 0)
            {
                // Not a completed login, so not a successful one; the success row is written when
                // the second factor lands.
                await _auditService.LogAsync(AuthAuditEventType.Login, assertionResult.SubjectId, success: false,
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    userAgent: Request.Headers.UserAgent.ToString(),
                    detailsJson: JsonSerializer.Serialize(new { method = "passkey", secondFactorPending = true }));
                Response.SetLastSignInCookie(
                    SessionCookieExtensions.SignInMethods.Passkey, providerId: null, _oidcOptions);

                return Ok(new PasskeyLoginCompleteResponse
                {
                    Success = true,
                    TotpRequired = true,
                    StepUpToken = await _totpService.CreateStepUpTokenAsync(
                        assertionResult.SubjectId, tenantId, admittedByInvite: false),
                });
            }

            var session = await _sessionService.IssueSessionAsync(
                assertionResult.SubjectId,
                new SessionContext(
                    DeviceDescription: "Passkey",
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: Request.Headers.UserAgent.ToString()));

            Response.SetSessionCookies(session, _oidcOptions);
            Response.SetLastSignInCookie(
                SessionCookieExtensions.SignInMethods.Passkey, providerId: null, _oidcOptions);

            await _auditService.LogAsync(AuthAuditEventType.Login, assertionResult.SubjectId, success: true,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { method = "passkey" }));

            return Ok(new PasskeyLoginCompleteResponse
            {
                Success = true,
                AccessToken = session.AccessToken,
                ExpiresIn = session.ExpiresInSeconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passkey login completion failed");

            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subjectId: null, success: false,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                errorMessage: ex.Message,
                detailsJson: JsonSerializer.Serialize(new { method = "passkey" }));

            return Problem(detail: "Passkey authentication failed", statusCode: 400, title: "Bad Request");
        }
    }

    /// <summary>
    /// Verify a recovery code and issue a restricted recovery session
    /// </summary>
    [HttpPost("recovery/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("passkey-recovery")]
    [RemoteCommand]
    [ProducesResponseType(typeof(RecoveryVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecoveryVerifyResponse>> RecoveryVerify(
        [FromBody] RecoveryVerifyRequest request)
    {
        await using var db = await _dbContextFactory.CreateAsync();
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Code))
        {
            return Problem(detail: "Username and recovery code are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        // Look up subject by username within the current tenant
        var subjectEntity = await db.TenantMembers
            .AsNoTracking()
            .Where(tm => tm.TenantId == tenantId)
            .Select(tm => tm.Subject)
            .FirstOrDefaultAsync(s => s != null && s.Username == request.Username);

        // Unknown usernames go through the same verification; see IRecoveryCodeService.VerifyAndConsumeAsync.
        var verified = await _recoveryCodeService.VerifyAndConsumeAsync(subjectEntity?.Id, request.Code);
        if (!verified)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subjectEntity?.Id, success: false,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { method = "recovery_code" }));
            return Problem(detail: "Invalid username or recovery code", statusCode: 400, title: "Bad Request");
        }

        var subjectId = subjectEntity!.Id;

        await _auditService.LogAsync(AuthAuditEventType.Login, subjectId, success: true,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { method = "recovery_code" }));

        StartRecoverySession(subjectEntity);

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
    /// Returns tenant auth status: whether setup is required or recovery mode is active.
    /// Queries the database for passkey credentials and orphaned subjects.
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(AuthStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthStatus()
    {
        await using var db = await _dbContextFactory.CreateAsync();
        var tenantId = _tenantAccessor.TenantId;

        var hasCredentials = await TenantHasCredentialsAsync(tenantId);
        var setupRequired = !hasCredentials;
        var recoveryMode = hasCredentials && await HasOrphanedSubjectAsync(tenantId);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);

        return Ok(new AuthStatusResponse
        {
            SetupRequired = setupRequired,
            RecoveryMode = recoveryMode,
            AllowAccessRequests = tenant?.AllowAccessRequests ?? false,
            OnboardingCompleted = tenant?.OnboardingCompletedAt != null,
        });
    }

    /// <summary>
    /// Whether any member of the tenant has a passkey or a linked provider, i.e. whether the
    /// tenant is past first-run setup.
    /// </summary>
    /// <remarks>
    /// System-pinned: <see cref="GetAuthStatus"/> is anonymous and reachable on a share host
    /// (<see cref="INocturneDbContextFactory.CreateSystemPinnedAsync"/>).
    /// </remarks>
    private async Task<bool> TenantHasCredentialsAsync(Guid tenantId)
    {
        await using var db = await _dbContextFactory.CreateSystemPinnedAsync(tenantId, HttpContext.RequestAborted);

        return await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                db.WorkingOidcIdentities().Any(o => o.SubjectId == m.SubjectId));
    }

    /// <summary>
    /// Whether the tenant has an active, non-system member with no passkey and no linked
    /// provider — an account that cannot sign in at all.
    /// </summary>
    /// <remarks>System-pinned as <see cref="TenantHasCredentialsAsync"/> is.</remarks>
    private async Task<bool> HasOrphanedSubjectAsync(Guid tenantId)
    {
        await using var db = await _dbContextFactory.CreateSystemPinnedAsync(tenantId, HttpContext.RequestAborted);

        return await db.OrphanedSubjectsOf(tenantId).AnyAsync();
    }

    /// <summary>
    /// Mark the current tenant's onboarding core as complete, enrolling it in the setup hub.
    /// </summary>
    [HttpPost("onboarding/complete")]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetAuthStatus"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CompleteOnboarding()
    {
        await using var db = await _dbContextFactory.CreateAsync();
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
            return NotFound();

        if (tenant.OnboardingCompletedAt == null)
        {
            tenant.OnboardingCompletedAt = DateTime.UtcNow;
            tenant.SetupHubEnrolledAt = tenant.OnboardingCompletedAt;
            await db.SaveChangesAsync();
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
        await using var db = await _dbContextFactory.CreateAsync();
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        var tenantId = _tenantAccessor.TenantId;
        var tenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.AllowAccessRequests)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Display name is required", statusCode: 400, title: "Bad Request");

        var displayName = request.DisplayName.Trim();
        var username = displayName.ToLowerInvariant().Replace(" ", "-");

        // Cancelling the OS prompt leaves the subject this step created behind. Resume that one
        // rather than adding another under the same name: it holds no credential, so there is
        // nothing to take over, and the complete step resolves the requestor by display name.
        var subjectId = await FindEnrollingSubjectIdAsync(db, AccessRequestEnrolmentMatch(displayName));

        if (subjectId == null)
        {
            var existingPending = await db.Subjects
                .AnyAsync(s => s.ApprovalStatus == "Pending" && s.Name == displayName);

            if (existingPending)
                return Conflict(new ProblemDetails
                {
                    Detail = "A pending access request with this name already exists",
                    Status = 409,
                    Title = "Conflict",
                });

            var subject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = displayName,
                Username = username,
                IsActive = false,
                IsSystemSubject = false,
                ApprovalStatus = "Pending",
                AccessRequestMessage = request.Message?.Trim(),
            };

            db.Subjects.Add(subject);
            subjectId = subject.Id;
        }
        else
        {
            var subject = await db.Subjects.FirstAsync(s => s.Id == subjectId.Value);
            subject.AccessRequestMessage = request.Message?.Trim();
        }

        await db.SaveChangesAsync();

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId.Value, username);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration for an anonymous access request.
    /// Verifies the attestation, stores the credential, and notifies tenant owners via
    /// <see cref="IInAppNotificationService"/>. The subject remains inactive until an owner approves.
    /// </summary>
    /// <remarks>
    /// The display name is re-resolved to the pending subject the options step created rather than
    /// trusted, so a registration challenge minted by another flow cannot be redeemed here.
    /// </remarks>
    /// <param name="request">The display name, attestation response, and challenge token from the WebAuthn ceremony.</param>
    /// <param name="notificationService">Injected notification service for alerting owners.</param>
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
        [FromServices] IInAppNotificationService notificationService)
    {
        await using var db = await _dbContextFactory.CreateAsync();
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.AllowAccessRequests)
            return NotFound();

        var displayName = request.DisplayName?.Trim();
        var enrollingSubjectId = string.IsNullOrEmpty(displayName)
            ? null
            : await FindEnrollingSubjectIdAsync(db, AccessRequestEnrolmentMatch(displayName));

        if (enrollingSubjectId == null)
            return Problem(detail: "Start the access request again", statusCode: 400, title: "Bad Request");

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenant.Id,
                expectedSubjectId: enrollingSubjectId.Value);

            var message = await db.Subjects
                .Where(s => s.Id == credResult.SubjectId)
                .Select(s => s.AccessRequestMessage)
                .FirstOrDefaultAsync();

            // System-pinned: "Request access" is rendered on a share host's login page too.
            await using var ownerCtx = await _dbContextFactory.CreateSystemPinnedAsync(
                tenant.Id, HttpContext.RequestAborted);
            var ownerIds = await ownerCtx.TenantMembers
                .OwnersOf(tenant.Id)
                .Select(tm => tm.SubjectId)
                .ToListAsync();

            foreach (var ownerId in ownerIds)
            {
                await notificationService.CreateNotificationAsync(
                    ownerId.ToString(),
                    "passkey.anonymous_login_request",
                    $"{displayName} has requested access",
                    subtitle: message != null && message.Length > 100 ? message[..100] : message,
                    sourceId: credResult.SubjectId.ToString(),
                    actions:
                    [
                        new NotificationActionDto
                        {
                            ActionId = "review",
                            Label = "Review",
                            Variant = "primary",
                        },
                    ],
                    metadata: new Dictionary<string, object>
                    {
                        ["navigateTo"] = "/settings/access-requests",
                    });
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Access request passkey registration failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");
        }
    }

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
        await using var db = await _dbContextFactory.CreateAsync();
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

        // Cancelling the OS prompt leaves the subject this step created behind. Reuse that one
        // rather than adding a second under the same username: the complete step resolves the
        // enrolling subject by username, and it holds no credential, so there is nothing to take
        // over.
        var subjectId = await FindEnrollingSubjectIdAsync(db, InviteEnrolmentMatch(username));

        if (subjectId == null)
        {
            var subject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = displayName,
                Username = username,
                IsActive = true,
                IsSystemSubject = false,
            };

            db.Subjects.Add(subject);
            subjectId = subject.Id;
        }
        else
        {
            var subject = await db.Subjects.FirstAsync(s => s.Id == subjectId.Value);
            subject.Name = displayName;
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Invite: enrolling subject {SubjectId} ({Username}) for invite acceptance",
            subjectId, username);

        // Generate passkey registration options
        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId.Value, username);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration for an invite acceptance.
    /// Verifies attestation, accepts the invite, generates recovery codes, and issues a session.
    /// </summary>
    /// <remarks>
    /// The username is re-resolved to the subject the options step created rather than trusted, so
    /// a registration challenge minted by another flow cannot be redeemed here. Only a subject with
    /// no sign-in method and no membership in any tenant can match.
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
        await using var db = await _dbContextFactory.CreateAsync();
        if (string.IsNullOrEmpty(request.ChallengeToken) || string.IsNullOrEmpty(request.Token)
            || string.IsNullOrWhiteSpace(request.Username))
        {
            return Problem(detail: "Username, challenge token, and invite token are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        var username = request.Username.Trim().ToLowerInvariant();
        var enrollingSubjectId = await FindEnrollingSubjectIdAsync(db, InviteEnrolmentMatch(username));

        if (enrollingSubjectId == null)
            return Problem(detail: "Start again from your invite link", statusCode: 400, title: "Bad Request");

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenantId,
                expectedSubjectId: enrollingSubjectId.Value);

            // Accept the invite
            var acceptResult = await memberInviteService.AcceptInviteAsync(
                request.Token, credResult.SubjectId, tenantId);
            if (!acceptResult.Success)
            {
                return Problem(detail: acceptResult.ErrorDescription ?? "Failed to accept invite", statusCode: 400, title: "Invite Error");
            }

            // Generate recovery codes
            var recoveryCodes = await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);

            var session = await _sessionService.IssueSessionAsync(
                credResult.SubjectId,
                new SessionContext(
                    DeviceDescription: "Invite Passkey",
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: Request.Headers.UserAgent.ToString()));

            Response.SetSessionCookies(session, _oidcOptions);

            _logger.LogInformation(
                "Invite complete: subject {SubjectId} registered with passkey via invite",
                credResult.SubjectId);

            return Ok(new PasskeyRegistrationResponse
            {
                Success = true,
                RecoveryCodes = recoveryCodes,
                AccessToken = session.AccessToken,
                RefreshToken = session.RefreshToken,
                ExpiresIn = session.ExpiresInSeconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invite passkey registration failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");
        }
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

/// <summary>
/// A recovery code an administrator issued for a member with no sign-in method left.
/// </summary>
public class RecoveryModeRedeemRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// A spent recovery-mode code. The account it names can now enrol one passkey.
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
/// Request to complete an anonymous access request. The display name is re-resolved against the
/// pending subject the options step created rather than trusted.
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
/// Request to complete an invite acceptance. The username is re-resolved against the subject the
/// options step created rather than trusted.
/// </summary>
public class InviteCompleteRequest
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

#endregion
