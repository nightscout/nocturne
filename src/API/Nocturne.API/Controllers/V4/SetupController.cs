using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
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
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-]{1,30}[a-z0-9]$")]
    private static partial Regex UsernamePattern();

    private static readonly HashSet<string> ReservedUsernames = ["admin", "system"];

    /// <summary>
    /// Create the first tenant on a fresh install. Only succeeds when zero tenants exist.
    /// </summary>
    [HttpPost("tenant")]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupTenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTenant(
        [FromBody] SetupTenantRequest request, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Check for tenants that have real members with credentials (passkey or OIDC).
        // The multitenancy migration seeds a 'default' tenant for backfilling, which
        // has no members and should not block fresh setup.
        var hasConfiguredTenant = await context.TenantMembers
            .AnyAsync(m =>
                context.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                context.SubjectOidcIdentities.Any(o => o.SubjectId == m.SubjectId), ct);
        if (hasConfiguredTenant)
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
    [RemoteQuery]
    [ProducesResponseType(typeof(SlugValidationResult), StatusCodes.Status200OK)]
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

        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            tenant.Id.ToString());

        var exists = await context.TenantMembers.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenant.Id && m.Username == normalized && m.RevokedAt == null, ct);

        if (exists)
            return Ok(new SlugValidationResult(false, "This username is already taken"));

        if (!string.IsNullOrEmpty(_operatorConfig.UsernameValidationWebhookUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("username-validation");
                var response = await client.PostAsJsonAsync(
                    _operatorConfig.UsernameValidationWebhookUrl,
                    new { username = normalized },
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<SlugValidationResult>(ct);
                    if (result is { IsValid: false })
                        return Ok(result);
                }
            }
            catch
            {
                // Webhook failure should not block validation — fall through to success
            }
        }

        return Ok(new SlugValidationResult(true));
    }

    /// <summary>
    /// Generate passkey registration options for the first owner account.
    /// Guard: exactly one tenant must exist with zero non-system members.
    /// </summary>
    [HttpPost("owner/options")]
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
            subjectId, normalizedUsername, tenant!.Id);

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

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenant!.Id);

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
            return Problem(detail: ex.Message, statusCode: 400, title: "Provider Error");
        }
    }

    /// <summary>
    /// OIDC callback for setup owner creation. Called by the OIDC provider after authentication.
    /// Links the identity, issues session cookies, and redirects to /setup.
    /// </summary>
    [HttpGet("oidc/callback")]
    [AllowAnonymous]
    [AllowDuringSetup]
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

        // Set RLS context to query tenant-scoped members table
        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            tenant.Id.ToString());

        var hasNonSystemMembers = await context.TenantMembers
            .Where(tm => tm.TenantId == tenant.Id)
            .Join(
                context.Subjects.Where(s => !s.IsSystemSubject),
                tm => tm.SubjectId,
                s => s.Id,
                (tm, s) => tm)
            .AnyAsync(ct);

        if (hasNonSystemMembers)
            return (null, Conflict(new { error = "owner_already_exists" }));

        return (tenant, null);
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

        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            tenant.Id.ToString());

        var existingSubject = await context.Subjects
            .FirstOrDefaultAsync(s => !s.IsSystemSubject && s.IsActive, ct);

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
        await memberCtx.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            tenant.Id.ToString());
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

    private void SetOidcStateCookie(string state, DateTimeOffset expiresAt)
    {
        var cookieSameSite = _oidcOptions.Cookie.SameSite switch
        {
            SameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            SameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            SameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
            _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        };

        Response.Cookies.Append(_oidcOptions.Cookie.StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = _oidcOptions.Cookie.Secure,
            SameSite = cookieSameSite,
            Path = _oidcOptions.Cookie.Path,
            Domain = _oidcOptions.Cookie.Domain,
            Expires = expiresAt,
        });
    }

    private void ClearOidcStateCookie()
    {
        Response.Cookies.Delete(_oidcOptions.Cookie.StateCookieName, new CookieOptions
        {
            Path = _oidcOptions.Cookie.Path,
            Domain = _oidcOptions.Cookie.Domain,
        });
    }

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
