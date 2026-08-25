using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.API.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Two-step setup endpoints for bootstrapping a fresh Nocturne install.
/// These operate without a resolved tenant and without authentication.
/// Step 1: Create the first tenant (POST /api/v4/setup/tenant).
/// Step 2: Create the owner account for that tenant (POST /api/v4/setup/owner/*).
/// </summary>
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
    private readonly ISessionService _sessionService;
    private readonly ISubjectService _subjectService;
    private readonly IDbContextFactory<NocturneDbContext> _dbFactory;
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
        ISessionService sessionService,
        ISubjectService subjectService,
        IDbContextFactory<NocturneDbContext> dbFactory,
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
        _sessionService = sessionService;
        _subjectService = subjectService;
        _dbFactory = dbFactory;
        _oidcOptions = oidcOptions.Value;
        _oidcAuthService = oidcAuthService;
        _operatorConfig = operatorConfig.Value;
        _httpClientFactory = httpClientFactory;
        _platformAdminBootstrap = platformAdminBootstrap;
        _setupState = setupState;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-]{1,30}[a-z0-9]$")]
    private static partial Regex UsernamePattern();

    private static readonly HashSet<string> ReservedUsernames = ["admin", "system"];

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

        var normalized = username.Trim().ToLowerInvariant();

        if (!UsernamePattern().IsMatch(normalized))
            return Ok(new SlugValidationResult(false,
                "Username must be 3-32 characters: letters, numbers, dots, underscores, and hyphens"));

        if (ReservedUsernames.Contains(normalized))
            return Ok(new SlugValidationResult(false, "This username is reserved"));

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var tenant = await context.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
        if (tenant == null)
            return Ok(new SlugValidationResult(false, "No tenant exists"));

        await context.PinTenantAsync(tenant.Id, ct);

        var exists = await context.TenantMembers.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenant.Id && m.Username == normalized, ct);

        if (exists)
            return Ok(new SlugValidationResult(false, "This username is already taken"));

        if (await AskUsernameWebhookAsync(normalized, ct) is { IsValid: false } refusal)
            return Ok(refusal);

        return Ok(new SlugValidationResult(true));
    }

    /// <summary>
    /// Generate passkey registration options for the first owner account.
    /// Guard: exactly one tenant must exist with zero non-system members.
    /// </summary>
    [HttpPost("owner/options")]
    [EnableRateLimiting("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupOwnerOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> OwnerOptions(
        [FromBody] SetupOwnerOptionsRequest request, CancellationToken ct)
    {
        var (tenant, error) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (error != null)
            return error;

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Username and display name are required", statusCode: 400, title: "Bad Request");

        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(normalizedUsername))
            return Problem(detail: "Username must be 3-32 characters: letters, numbers, dots, underscores, and hyphens",
                statusCode: 400, title: "Bad Request");

        if (ReservedUsernames.Contains(normalizedUsername))
            return Problem(detail: "This username is reserved", statusCode: 400, title: "Bad Request");

        var subjectId = await EnsureOwnerSubjectAsync(
            tenant!, request.DisplayName.Trim(), normalizedUsername, ct);

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId, normalizedUsername);

        return Ok(new SetupOwnerOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
            TenantId = tenant!.Id,
        });
    }

    /// <summary>
    /// Complete passkey registration for the first owner account.
    /// Verifies attestation, generates recovery codes, issues a full JWT session.
    /// </summary>
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

        // The enrolling subject is looked up here rather than taken from the challenge token, so a
        // registration challenge minted by another flow cannot be redeemed as the first owner.
        var ownerSubjectId = await FindSetupOwnerSubjectIdAsync(tenant!, ct);
        if (ownerSubjectId == null)
            return Problem(detail: "Start setup again to create the owner account",
                statusCode: 400, title: "Bad Request");

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenant!.Id,
                expectedSubjectId: ownerSubjectId.Value);

            await GrantFirstOwnerPlatformAdminAsync(credResult.SubjectId);

            // Generate recovery codes
            var recoveryCodes = await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);

            var session = await _sessionService.IssueSessionAsync(
                credResult.SubjectId,
                new SessionContext(
                    DeviceDescription: "Setup Passkey",
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: Request.Headers.UserAgent.ToString()));

            Response.SetSessionCookies(session, _oidcOptions);

            _logger.LogInformation(
                "Setup complete: first owner {SubjectId} registered with passkey for tenant {TenantId}",
                credResult.SubjectId, tenant!.Id);

            return Ok(new SetupOwnerCompleteResponse
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
            _logger.LogWarning(ex, "Setup owner passkey registration failed");
            return Problem(detail: "Passkey registration failed during setup", statusCode: 400, title: "Registration Failed");
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
    public async Task<IActionResult> OwnerOidc(
        [FromBody] SetupOwnerOidcRequest request, CancellationToken ct)
    {
        var (tenant, error) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (error != null)
            return error;

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Username and display name are required", statusCode: 400, title: "Bad Request");

        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(normalizedUsername))
            return Problem(detail: "Username must be 3-32 characters: letters, numbers, dots, underscores, and hyphens",
                statusCode: 400, title: "Bad Request");

        if (ReservedUsernames.Contains(normalizedUsername))
            return Problem(detail: "This username is reserved", statusCode: 400, title: "Bad Request");

        if (request.ProviderId == Guid.Empty)
            return Problem(detail: "Provider ID is required", statusCode: 400, title: "Bad Request");

        var subjectId = await EnsureOwnerSubjectAsync(
            tenant!, request.DisplayName.Trim(), normalizedUsername, ct);

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
    /// Links the identity, issues session cookies, and redirects to /setup.
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
        var (_, setupClosed) = await GetSoleTenantWithoutOwnerAsync(ct);
        if (setupClosed != null)
        {
            ClearOidcStateCookie();
            return Redirect("/setup?error=setup_already_complete");
        }

        var expectedState = Request.Cookies[_oidcOptions.Cookie.StateCookieName];
        ClearOidcStateCookie();

        if (string.IsNullOrEmpty(expectedState))
            return Redirect("/setup?error=missing_state");

        var result = await _oidcAuthService.HandleSetupCallbackAsync(
            code, state, expectedState,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString());

        if (!result.Success)
        {
            _logger.LogWarning(
                "Setup OIDC callback failed: {Error} - {Description}",
                result.Error, result.ErrorDescription);
            return Redirect($"/setup?error={Uri.EscapeDataString(result.Error ?? "unknown")}");
        }

        if (result.SubjectId is { } oidcOwnerSubjectId)
            await GrantFirstOwnerPlatformAdminAsync(oidcOwnerSubjectId);

        // Temporary bridge: construct SessionTokenPair from OidcTokenResponse until
        // OidcAuthService is migrated to ISessionService.
        var sessionPair = new SessionTokenPair(
            result.Tokens!.AccessToken,
            result.Tokens.RefreshToken,
            result.Tokens.ExpiresIn);
        Response.SetSessionCookies(sessionPair, _oidcOptions);

        return Redirect(result.ReturnUrl ?? "/setup");
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

        Exception? failure = null;
        int? statusCode = null;

        try
        {
            var client = _httpClientFactory.CreateClient("username-validation");
            var response = await client.PostAsJsonAsync(
                _operatorConfig.UsernameValidationWebhookUrl,
                new { username },
                ct);

            if (response.IsSuccessStatusCode)
            {
                Interlocked.Exchange(ref _usernameWebhookOutageReported, 0);
                return await response.Content.ReadFromJsonAsync<SlugValidationResult>(ct);
            }

            statusCode = (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                return null;

            failure = ex;
        }

        if (Interlocked.Exchange(ref _usernameWebhookOutageReported, 1) == 0)
        {
            _logger.LogWarning(
                failure,
                "Username validation webhook did not answer ({Outcome}); usernames are being accepted without it",
                statusCode?.ToString() ?? "unreachable");
        }

        return null;
    }

    /// <summary>
    /// Returns the sole tenant if exactly one exists and it has no non-system members,
    /// or an error result if the preconditions are not met.
    /// </summary>
    private async Task<(TenantEntity? Tenant, IActionResult? Error)> GetSoleTenantWithoutOwnerAsync(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var tenants = await context.Tenants.Take(2).ToListAsync(ct);

        if (tenants.Count == 0)
            return (null, Conflict(new { error = "no_tenant_exists" }));

        if (tenants.Count > 1)
            return (null, Conflict(new { error = "setup_already_complete" }));

        var tenant = tenants[0];

        // A credential-less member is a half-finished setup left behind by an abandoned or failed
        // WebAuthn/OIDC ceremony — EnsureOwnerSubjectAsync reuses that subject idempotently, so
        // let the flow resume instead of dead-ending on owner_already_exists.
        var hasOwnerWithCredentials = await _setupState.TenantHasCredentialedMemberAsync(tenant.Id, ct);

        if (hasOwnerWithCredentials)
            return (null, Conflict(new { error = "owner_already_exists" }));

        return (tenant, null);
    }

    /// <summary>
    /// The first-run owner subject: the earliest non-system active subject. Setup only runs while
    /// no member of the tenant holds credentials, so this is the account the owner options step
    /// created or reused. Ordered so the options and complete steps resolve the same row.
    /// </summary>
    private static IQueryable<SubjectEntity> SetupOwnerSubjects(NocturneDbContext context) =>
        context.Subjects.Where(s => !s.IsSystemSubject && s.IsActive).OrderBy(s => s.Id);

    /// <summary>
    /// Returns the first-run owner subject's id as resolved from the database, or
    /// <see langword="null"/> when the options step has not created it yet.
    /// </summary>
    private async Task<Guid?> FindSetupOwnerSubjectIdAsync(TenantEntity tenant, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        await context.PinTenantAsync(tenant.Id, ct);

        return await SetupOwnerSubjects(context)
            .AsNoTracking()
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Find or create the first non-system subject, ensure it is a member of the
    /// given tenant with the owner role, and assign the global admin role.
    /// Idempotent: safe to call on retries after a failed WebAuthn/OIDC ceremony,
    /// and when reusing a subject created for a previously deleted tenant.
    /// </summary>
    private async Task<Guid> EnsureOwnerSubjectAsync(
        TenantEntity tenant, string displayName, string username, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        await context.PinTenantAsync(tenant.Id, ct);

        var existingSubject = await SetupOwnerSubjects(context).FirstOrDefaultAsync(ct);

        Guid subjectId;
        if (existingSubject != null)
        {
            subjectId = existingSubject.Id;
            existingSubject.Name = displayName;
            existingSubject.Username = username;
            await context.SaveChangesAsync(ct);
        }
        else
        {
            subjectId = Guid.CreateVersion7();
            context.Subjects.Add(new SubjectEntity
            {
                Id = subjectId,
                Name = displayName,
                Username = username,
                IsActive = true,
                IsSystemSubject = false,
            });
            await context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Setup: created owner {SubjectId} ({Username}) for tenant {TenantId}",
                subjectId, username, tenant.Id);
        }

        // Ensure tenant membership with owner role — required when reusing a
        // subject from a previously deleted tenant.
        var ownerRole = await context.TenantRoles
            .FirstOrDefaultAsync(r => r.TenantId == tenant.Id && r.Slug == "owner", ct);
        if (ownerRole != null)
        {
            var isMember = await context.TenantMembers
                .AnyAsync(m => m.TenantId == tenant.Id && m.SubjectId == subjectId, ct);
            if (!isMember)
                await _tenantService.AddMemberAsync(tenant.Id, subjectId, [ownerRole.Id], ct: ct);
        }

        // Set per-tenant username on the membership
        await using var memberCtx = await _dbFactory.CreateDbContextAsync(ct);
        await memberCtx.PinTenantAsync(tenant.Id, ct);
        var membership = await memberCtx.TenantMembers
            .FirstOrDefaultAsync(m => m.TenantId == tenant.Id && m.SubjectId == subjectId, ct);
        if (membership != null)
        {
            membership.Username = username;
            await memberCtx.SaveChangesAsync(ct);
        }

        await _subjectService.AssignRoleAsync(subjectId, "admin");

        return subjectId;
    }

    /// <summary>
    /// Grants the new owner platform admin now that it holds a credential. The startup
    /// bootstrap pass ran against an empty database on a fresh install, so without this
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
