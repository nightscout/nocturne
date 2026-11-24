using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.API.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// The first-run setup of a new install. A caller creates the first tenant, checks whether a
/// username can be used, and then creates the owner account for that tenant. These endpoints need
/// no sign-in while setup is not complete.
/// </summary>
/// <remarks>
/// These operate without a resolved tenant and without authentication.
/// Step 1: Create the first tenant (POST /api/v4/setup/tenant).
/// Step 2: Create the owner account for that tenant (POST /api/v4/setup/owner/*).
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/setup")]
[Produces("application/json")]
[AllowAnonymous]
[AllowDuringSetup]
public partial class SetupController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly IPasskeyService _passkeyService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly SignInCompletion _signIn;
    private readonly ISubjectService _subjectService;
    private readonly ISubjectRepository _subjects;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly OidcOptions _oidcOptions;
    private readonly IOidcAuthService _oidcAuthService;
    private readonly OperatorConfiguration _operatorConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlatformAdminBootstrapService _platformAdminBootstrap;
    private readonly IInstanceSetupState _setupState;
    private readonly ILogger<SetupController> _logger;

    public SetupController(
        ITenantService tenantService,
        IPasskeyService passkeyService,
        IRecoveryCodeService recoveryCodeService,
        SignInCompletion signIn,
        ISubjectService subjectService,
        ISubjectRepository subjects,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        IOptions<OidcOptions> oidcOptions,
        IOidcAuthService oidcAuthService,
        IOptions<OperatorConfiguration> operatorConfig,
        IHttpClientFactory httpClientFactory,
        PlatformAdminBootstrapService platformAdminBootstrap,
        IInstanceSetupState setupState,
        ILogger<SetupController> logger)
    {
        _tenantService = tenantService;
        _passkeyService = passkeyService;
        _recoveryCodeService = recoveryCodeService;
        _signIn = signIn;
        _subjectService = subjectService;
        _subjects = subjects;
        _tenants = tenants;
        _members = members;
        _oidcOptions = oidcOptions.Value;
        _oidcAuthService = oidcAuthService;
        _operatorConfig = operatorConfig.Value;
        _httpClientFactory = httpClientFactory;
        _platformAdminBootstrap = platformAdminBootstrap;
        _setupState = setupState;
        _logger = logger;
    }

    /// <summary>
    /// Whether the username webhook's last attempt has already been reported. See
    /// <see cref="AskUsernameWebhookAsync"/>.
    /// </summary>
    private static int _usernameWebhookOutageReported;

    /// <summary>
    /// Create the first tenant on a fresh install. Only succeeds when zero tenants exist.
    /// </summary>
    [HttpPost("tenant")]
    [EnableRateLimiting("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupTenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTenant(
        [FromBody] SetupTenantRequest request, CancellationToken ct)
    {
        if (await _setupState.IsSetupCompleteAsync(ct))
            return Conflict(new { error = "setup_already_complete" });

        if (string.IsNullOrWhiteSpace(request.Slug) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Slug and display name are required", statusCode: 400, title: "Bad Request");

        var validation = await _tenantService.ValidateSlugAsync(request.Slug, ct);
        if (!validation.IsValid)
            return Problem(detail: validation.Message, statusCode: 400, title: "Bad Request");

        var result = await _tenantService.CreateWithoutOwnerAsync(
            request.Slug, request.DisplayName, ct: ct);

        return Ok(new SetupTenantResponse(result.Id));
    }

    /// <summary>
    /// Check whether a username is available for the owner account.
    /// </summary>
    [HttpGet("validate-username")]
    [AnonymousUntilSetupComplete]
    [DenyDemoSubject]
    [EnableRateLimiting("name-availability")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SlugValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ValidateUsername(
        [FromQuery] string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Ok(new SlugValidationResult(false, "Username is required"));

        var normalized = UsernameRules.Normalize(username);

        if (UsernameRules.Refusal(normalized) is { } formatRefusal)
            return Ok(new SlugValidationResult(false, formatRefusal));

        var tenant = (await _tenants.ListNonDemoAsync(1, ct)).FirstOrDefault();
        if (tenant == null)
            return Ok(new SlugValidationResult(false, "No tenant exists"));

        if (await _members.UsernameExistsAsync(tenant.Id, normalized, ct))
            return Ok(new SlugValidationResult(false, "This username is already taken"));

        if (await AskUsernameWebhookAsync(normalized, ct) is { IsValid: false } refusal)
            return Ok(refusal);

        return Ok(new SlugValidationResult(true));
    }

    /// <summary>
    /// Generate passkey registration options for the first owner account.
    /// Guard: <see cref="GetSoleTenantWithoutOwnerAsync"/>.
    /// </summary>
    [HttpPost("owner/options")]
    [EnableRateLimiting("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupOwnerOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> OwnerOptions(
        [FromBody] SetupOwnerOptionsRequest request, CancellationToken ct)
    {
        if (this.PasskeyHostRefusal(_passkeyService) is { } refusal)
            return refusal;

        var (tenant, error) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (error != null)
            return error;

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Username and display name are required", statusCode: 400, title: "Bad Request");

        var normalizedUsername = UsernameRules.Normalize(request.Username);
        if (UsernameRules.Refusal(normalizedUsername) is { } usernameRefusal)
            return Problem(detail: usernameRefusal, statusCode: 400, title: "Bad Request");

        var (subjectId, ownerRefusal) =
            await EnsureOwnerSubjectAsync(tenant!, request.DisplayName.Trim(), normalizedUsername, ct);
        if (ownerRefusal != null)
            return ownerRefusal;

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId, normalizedUsername, PasskeyRegistrationFlow.SetupOwner);

        return Ok(new SetupOwnerOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
            TenantId = tenant!.Id,
        });
    }

    /// <summary>
    /// Complete passkey registration for the first owner account.
    /// Verifies attestation, ends setup, signs the owner in, and generates recovery codes.
    /// </summary>
    /// <remarks>
    /// Setup closes when the passkey is stored (<see cref="IInstanceSetupState.TenantHasCredentialedMemberAsync"/>),
    /// and the store claims the setup (<see cref="IPasskeyCredentialRepository.CreateForSetupOwnerAsync"/>), so
    /// of two completions under way at once only one stores a passkey; the other is answered as a
    /// completion that arrives after setup closed. A failure after the store leaves an owner who
    /// signs in with the passkey on the login page, and that sign-in gives them their codes
    /// (<see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>).
    /// </remarks>
    [HttpPost("owner/complete")]
    [EnableRateLimiting("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupOwnerCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> OwnerComplete(
        [FromBody] SetupOwnerCompleteRequest request, CancellationToken ct)
    {
        var (tenant, error) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (error != null)
            return error;

        if (string.IsNullOrEmpty(request.ChallengeToken))
            return Problem(detail: "Challenge token is required", statusCode: 400, title: "Bad Request");

        // The enrolling subject is looked up here rather than taken from the challenge token, and
        // the challenge must have been minted by OwnerOptions (PasskeyRegistrationFlow.SetupOwner).
        var ownerSubjectId = await FindSetupOwnerSubjectIdAsync(tenant!, ct);
        if (ownerSubjectId == null)
            return StartSetupAgain();

        PasskeyCredentialResult credResult;
        try
        {
            credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, PasskeyRegistrationFlow.SetupOwner, tenant!.Id,
                expectedSubjectId: ownerSubjectId.Value);
        }
        catch (InvalidOperationException ex)
        {
            return RegistrationFailed(ex);
        }
        catch (JsonException ex)
        {
            return RegistrationFailed(ex);
        }
        catch (PasskeyRegistrationRefusedException ex)
        {
            return RegistrationFailed(ex);
        }
        catch (SetupOwnerTakenException)
        {
            return OwnerAlreadyExists();
        }

        await GrantFirstOwnerPlatformAdminAsync(credResult.SubjectId);

        var outcome = await SignInOwnerAsync(
            credResult.SubjectId, tenant!, SignInMethod.Passkey, "Setup Passkey");

        switch (outcome)
        {
            case SignInOutcome.SignedIn signedIn:
                var recoveryCodes = await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);
                _logger.LogInformation(
                    "Setup complete: first owner {SubjectId} registered with passkey for tenant {TenantId}",
                    credResult.SubjectId, tenant!.Id);
                return Ok(new SetupOwnerCompleteResponse
                {
                    Success = true,
                    RecoveryCodes = recoveryCodes,
                    AccessToken = signedIn.Session.AccessToken,
                    RefreshToken = signedIn.Session.RefreshToken,
                    ExpiresIn = signedIn.Session.ExpiresInSeconds,
                });
            case SignInOutcome.SecondFactorRequired:
                return OwnerAlreadyExists();
            case SignInOutcome.Refused { Reason: SignInRefusal.NotAMember }:
                return StartSetupAgain();
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>
    /// Initiate OIDC-based owner creation. Creates the subject and owner role,
    /// then redirects to the OIDC provider to link an identity.
    /// </summary>
    [HttpPost("owner/oidc")]
    [EnableRateLimiting("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupOwnerOidcResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> OwnerOidc(
        [FromBody] SetupOwnerOidcRequest request, CancellationToken ct)
    {
        var (tenant, error) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (error != null)
            return error;

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Username and display name are required", statusCode: 400, title: "Bad Request");

        var normalizedUsername = UsernameRules.Normalize(request.Username);
        if (UsernameRules.Refusal(normalizedUsername) is { } usernameRefusal)
            return Problem(detail: usernameRefusal, statusCode: 400, title: "Bad Request");

        if (request.ProviderId == Guid.Empty)
            return Problem(detail: "Provider ID is required", statusCode: 400, title: "Bad Request");

        var (subjectId, ownerRefusal) =
            await EnsureOwnerSubjectAsync(tenant!, request.DisplayName.Trim(), normalizedUsername, ct);
        if (ownerRefusal != null)
            return ownerRefusal;

        try
        {
            var authRequest = await _oidcAuthService.GenerateSetupAuthorizationUrlAsync(
                request.ProviderId, subjectId, tenant!.Slug);

            SetOidcStateCookie(authRequest.State, authRequest.ExpiresAt);

            return Ok(new SetupOwnerOidcResponse
            {
                AuthorizationUrl = authRequest.AuthorizationUrl,
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to generate setup OIDC authorization URL");
            return Problem(
                detail: "We couldn't reach that sign-in provider. Try another one, or check the provider settings.",
                statusCode: 400);
        }
    }

    /// <summary>
    /// OIDC callback for setup owner creation. Called by the OIDC provider after authentication.
    /// Links the identity, ends setup, signs the owner in, and redirects to /setup.
    /// </summary>
    [HttpGet("oidc/callback")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [EnableRateLimiting("setup")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> OidcCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Setup OIDC provider returned error: {Error} - {Description}", error, error_description);
            ClearOidcStateCookie();
            return Redirect($"/setup?error={Uri.EscapeDataString(error)}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            ClearOidcStateCookie();
            return Redirect("/setup?error=missing_parameters");
        }

        // The sibling setup endpoints all gate on this; without it the callback is the one
        // setup route that stays live on an established instance, and it both links an
        // identity and issues a session for whatever subject the state names.
        var (tenant, setupClosed) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (setupClosed != null)
        {
            ClearOidcStateCookie();
            return Redirect("/setup?error=setup_already_complete");
        }

        var expectedState = Request.Cookies[_oidcOptions.Cookie.StateCookieName];
        ClearOidcStateCookie();

        if (string.IsNullOrEmpty(expectedState))
            return Redirect("/setup?error=missing_state");

        var result = await _oidcAuthService.HandleSetupCallbackAsync(code, state, expectedState, tenant!.Id);

        if (!result.Success || result.SubjectId is not { } ownerSubjectId)
        {
            _logger.LogWarning(
                "Setup OIDC callback failed: {Error} - {Description}",
                result.Error, result.ErrorDescription);
            return Redirect($"/setup?error={Uri.EscapeDataString(result.Error ?? "unknown")}");
        }

        await GrantFirstOwnerPlatformAdminAsync(ownerSubjectId);

        var outcome = await SignInOwnerAsync(
            ownerSubjectId, tenant!, SignInMethod.Oidc, "Setup OIDC", result.OidcSessionId, result.ProviderId);

        return outcome switch
        {
            SignInOutcome.SignedIn => Redirect(result.ReturnUrl ?? "/setup"),
            SignInOutcome.SecondFactorRequired => Redirect("/setup?error=setup_already_complete"),
            SignInOutcome.Refused { Reason: SignInRefusal.NotAMember } => Redirect("/setup?error=not_a_member"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }

    #region Private Helpers

    /// <summary>
    /// The operator's username-validation webhook's verdict, or <see langword="null"/> when no
    /// webhook is configured or it could not answer.
    /// </summary>
    /// <remarks>
    /// Fails open. The webhook is an operator's extra restriction layered on the checks above, and
    /// only this advisory probe consults it — the owner-creation steps never do, so refusing here
    /// would not keep the name from being taken. An outage that refused every username instead
    /// would strand the operator on the one screen with no way past it, on an instance that has no
    /// account yet to correct the configuration from.
    /// <para>
    /// The endpoint is anonymous, so a caller sets the request volume; one log line per outage
    /// keeps that from being a log-volume lever. The latch reopens when the webhook next answers.
    /// </para>
    /// </remarks>
    private async Task<SlugValidationResult?> AskUsernameWebhookAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_operatorConfig.UsernameValidationWebhookUrl))
            return null;

        var answer = await OperatorWebhook.PostAsync<SlugValidationResult>(
            _httpClientFactory.CreateClient(OperatorWebhook.UsernameValidationClient),
            _operatorConfig.UsernameValidationWebhookUrl,
            new { username },
            ct);

        if (answer.Outcome == OperatorWebhookOutcome.Answered)
        {
            Interlocked.Exchange(ref _usernameWebhookOutageReported, 0);
            return answer.Value;
        }

        if (Interlocked.Exchange(ref _usernameWebhookOutageReported, 1) == 0)
        {
            _logger.LogWarning(
                "Username validation webhook did not answer ({Outcome}, {Detail}); usernames are being accepted without it",
                answer.StatusCode?.ToString() ?? answer.Outcome.ToString(),
                answer.Detail);
        }

        return null;
    }

    /// <summary>
    /// Returns the sole non-demo tenant if exactly one exists and no member of it holds a passkey
    /// or a working provider identity (<see cref="IInstanceSetupState.TenantHasCredentialedMemberAsync"/>),
    /// or an error result if the preconditions are not met.
    /// </summary>
    /// <remarks>
    /// A demo tenant never trips the credentialed-member arm (<see cref="Nocturne.Infrastructure.Data.Extensions.DemoExclusionFilter"/>),
    /// so a demo-only instance answers <c>no_tenant_exists</c>, from which a tenant can still be
    /// created, rather than <c>setup_already_complete</c>, which would strand the operator on a
    /// tenant nobody can adopt.
    /// </remarks>
    private async Task<(TenantRecord? Tenant, IActionResult? Error)> GetSoleTenantWithoutOwnerAsync(CancellationToken ct)
    {
        var tenants = await _tenants.ListNonDemoAsync(2, ct);

        if (tenants.Count == 0)
            return (null, Conflict(new { error = "no_tenant_exists" }));

        if (tenants.Count > 1)
            return (null, Conflict(new { error = "setup_already_complete" }));

        var tenant = tenants[0];

        var hasOwnerWithCredentials = await _setupState.TenantHasCredentialedMemberAsync(tenant.Id, ct);

        if (hasOwnerWithCredentials)
            return (null, OwnerAlreadyExists());

        return (tenant, null);
    }

    /// <summary>
    /// Returns the first-run owner subject's id
    /// (<see cref="ISubjectRepository.FindSetupOwnerCandidateAsync"/>), or <see langword="null"/>
    /// when the options step has not created it yet.
    /// </summary>
    private async Task<Guid?> FindSetupOwnerSubjectIdAsync(TenantRecord tenant, CancellationToken ct) =>
        (await _subjects.FindSetupOwnerCandidateAsync(tenant.Id, ct))?.Id;

    /// <summary>
    /// Find (<see cref="ISubjectRepository.FindSetupOwnerCandidateAsync"/>) or create the owner
    /// subject, ensure it is a member of the given tenant with the owner role, and assign the
    /// global admin role. Idempotent: safe to call on retries after a failed WebAuthn/OIDC ceremony.
    /// </summary>
    /// <returns>
    /// The subject, or a refusal with nothing written: <see cref="UsernameTaken"/> when another member
    /// of the tenant holds <paramref name="username"/> (setup reopens while former members still hold
    /// their usernames), or <see cref="OwnerRoleMissing"/> when the tenant has no owner role.
    /// </returns>
    private async Task<(Guid SubjectId, ObjectResult? Refusal)> EnsureOwnerSubjectAsync(
        TenantRecord tenant, string displayName, string username, CancellationToken ct)
    {
        if (await _members.FindRoleIdBySlugAsync(tenant.Id, RoleSeeds.Owner, ct) is not { } roleId)
        {
            _logger.LogError("Setup: tenant {TenantId} has no owner role, so no owner can be created", tenant.Id);
            return (default, OwnerRoleMissing());
        }

        var existingSubject = await _subjects.FindSetupOwnerCandidateAsync(tenant.Id, ct);

        if (await _members.UsernameExistsAsync(tenant.Id, username, ct)
            && (existingSubject is null
                || (await _members.FindMemberAsync(tenant.Id, existingSubject.Id, ct))?.Username != username))
            return (default, UsernameTaken());

        Guid subjectId;
        if (existingSubject != null)
        {
            subjectId = existingSubject.Id;
            await _subjects.UpdateAsync(subjectId, new SubjectEdit(Name: displayName, Username: username), ct);
        }
        else
        {
            subjectId = await _subjects.CreateSetupOwnerAsync(
                tenant.Id, new NewSubject(displayName, Username: username), ct);

            _logger.LogInformation(
                "Setup: created owner {SubjectId} ({Username}) for tenant {TenantId}",
                subjectId, username, tenant.Id);
        }

        // False means the subject is already a member, which a retry wants; any other failed add throws.
        _ = await _members.AddMemberAsync(
            new NewTenantMember(
                tenant.Id, subjectId, [roleId], MadeBy: MembershipMadeBy.Member) { How = MembershipJoinHow.Setup, By = null },
            ct);

        // Set per-tenant username on the membership
        if (await _members.FindMemberAsync(tenant.Id, subjectId, ct) is { } membership)
            await _members.SetUsernameAsync(tenant.Id, membership.Id, username, ct);

        await _subjectService.AssignRoleAsync(subjectId, GlobalRoles.Admin);

        return (subjectId, null);
    }

    /// <summary>
    /// Grants the new owner platform admin now that it holds a credential. The startup
    /// bootstrap pass ran against an empty database on a fresh install, so without the grant
    /// the owner cannot reach /settings/admin until the API restarts.
    /// </summary>
    /// <remarks>
    /// Deliberately called from the completion paths rather than
    /// <see cref="EnsureOwnerSubjectAsync"/>: an abandoned WebAuthn ceremony would
    /// otherwise leave a credential-less subject holding the flag, which then suppresses
    /// every later grant — including the startup pass — and locks the instance out for good.
    /// </remarks>
    private async Task GrantFirstOwnerPlatformAdminAsync(Guid subjectId)
    {
        // Not the request token: a client that disconnects mid-response would otherwise
        // abort the grant and leave the owner locked out of the admin UI.
        await _platformAdminBootstrap.EnsureFirstOwnerIsPlatformAdminAsync(
            subjectId, CancellationToken.None);
    }

    /// <summary>
    /// Signs in the owner setup has just completed, and writes the cookies the outcome calls for.
    /// </summary>
    /// <remarks>
    /// <see cref="SignInAdmission.Member"/>: the owner step made the membership before the
    /// ceremony, so it exists here and the sign-in checks it.
    /// <para>
    /// The gate answers <see cref="SecondFactorRequirement.NotEnrolled"/>: the owner candidate and an
    /// adopted subject hold no authenticator, and one is added only from a session. Were a code
    /// owed, no session is issued and setup still ends: the owner holds a credential and signs in
    /// on the login page, so the caller answers as setup answers once an owner exists.
    /// </para>
    /// </remarks>
    private async Task<SignInOutcome> SignInOwnerAsync(
        Guid subjectId, TenantRecord tenant, SignInMethod method, string deviceDescription,
        string? oidcSessionId = null, Guid? providerId = null)
    {
        var outcome = await _signIn.CompleteAsync(new SignInAttempt(
            subjectId,
            method,
            new SignInAdmission.Member(tenant.Id),
            new SecondFactorProof.Primary(Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName]),
            new SessionContext(
                OidcSessionId: oidcSessionId,
                DeviceDescription: deviceDescription,
                IpAddress: HttpContext.GetClientAddress(),
                UserAgent: Request.Headers.UserAgent.ToString()),
            providerId,
            Enrolment: SignInEnrolment.Setup));

        await HttpContext.ApplySignInAsync(outcome, _oidcOptions);

        if (outcome is not SignInOutcome.SignedIn)
            _logger.LogWarning(
                "Setup: owner {SubjectId} completed setup but was not signed in ({Outcome})",
                subjectId, outcome.GetType().Name);

        return outcome;
    }

    /// <summary>
    /// The answer to a refused setup registration. The refusal types are the ones
    /// <see cref="IPasskeyService.CompleteRegistrationAsync"/> names.
    /// </summary>
    private ObjectResult RegistrationFailed(Exception ex)
    {
        _logger.LogWarning(ex, "Setup owner passkey registration failed");
        return Problem(detail: "Passkey registration failed during setup", statusCode: 400, title: "Registration Failed");
    }

    private ConflictObjectResult OwnerAlreadyExists() => Conflict(new { error = "owner_already_exists" });

    private ObjectResult StartSetupAgain() =>
        Problem(detail: "Start setup again to create the owner account", statusCode: 400, title: "Bad Request");

    private ObjectResult UsernameTaken() =>
        Problem(detail: "This username is already taken", statusCode: 400, title: "Bad Request");

    private ObjectResult OwnerRoleMissing() =>
        Problem(
            detail: "This tenant has no owner role, so setup cannot create the owner account.",
            statusCode: 500, title: "Internal Server Error");

    // Through the shared writer: the setup flow reuses the login flow's state-cookie name, so a
    // scope of its own would leave two same-named cookies the callback cannot tell apart.
    private void SetOidcStateCookie(string state, DateTimeOffset expiresAt) =>
        Response.SetStateCookie(_oidcOptions.Cookie.StateCookieName, state, expiresAt, _oidcOptions);

    private void ClearOidcStateCookie() =>
        Response.ClearStateCookie(_oidcOptions.Cookie.StateCookieName, _oidcOptions);

    #endregion
}

#region Request/Response DTOs

public record ValidateSlugRequest(string Slug);

public record SetupTenantRequest(string Slug, string DisplayName);

public record SetupTenantResponse(Guid TenantId);

public class SetupOwnerOptionsRequest
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class SetupOwnerOptionsResponse
{
    public string Options { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
}

public class SetupOwnerCompleteRequest
{
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

public class SetupOwnerCompleteResponse
{
    public bool Success { get; set; }
    public List<string> RecoveryCodes { get; set; } = new();
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
}

public class SetupOwnerOidcRequest
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Guid ProviderId { get; set; }
}

public class SetupOwnerOidcResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
}

#endregion
