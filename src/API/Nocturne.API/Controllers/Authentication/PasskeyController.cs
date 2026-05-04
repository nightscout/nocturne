using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Controller for WebAuthn/FIDO2 passkey authentication ceremonies.
/// Handles registration, login (both discoverable and non-discoverable), and recovery code verification.
/// </summary>
/// <remarks>
/// Authentication flows:
/// <list type="bullet">
///   <item><description><b>Registration:</b> <c>POST /register/options</c> → <c>POST /register/complete</c></description></item>
///   <item><description><b>Discoverable login</b> (no username): <c>POST /login/discoverable/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Non-discoverable login</b> (with username): <c>POST /login/options</c> → <c>POST /login/complete</c></description></item>
///   <item><description><b>Recovery:</b> <c>POST /recovery/verify</c> issues a 10-minute restricted token allowing passkey management only.</description></item>
///   <item><description><b>Initial setup:</b> <c>POST /setup/options</c> → <c>POST /setup/complete</c> (only available before any passkeys exist).</description></item>
///   <item><description><b>Invite acceptance:</b> <c>POST /invite/options</c> → <c>POST /invite/complete</c> using a pre-issued invite token.</description></item>
/// </list>
///
/// On successful login or setup, the controller uses
/// <see cref="SessionCookieExtensions.SetSessionCookies"/> to set session cookies.
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

    private readonly IPasskeyService _passkeyService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly IJwtService _jwtService;
    private readonly ISessionService _sessionService;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantService _tenantService;
    private readonly NocturneDbContext _dbContext;
    private readonly OidcOptions _oidcOptions;
    private readonly ILogger<PasskeyController> _logger;

    /// <summary>
    /// Creates a new instance of PasskeyController
    /// </summary>
    public PasskeyController(
        IPasskeyService passkeyService,
        IRecoveryCodeService recoveryCodeService,
        IJwtService jwtService,
        ISessionService sessionService,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        ITenantAccessor tenantAccessor,
        ITenantService tenantService,
        NocturneDbContext dbContext,
        IOptions<OidcOptions> oidcOptions,
        ILogger<PasskeyController> logger)
    {
        _passkeyService = passkeyService;
        _recoveryCodeService = recoveryCodeService;
        _jwtService = jwtService;
        _sessionService = sessionService;
        _subjectService = subjectService;
        _auditService = auditService;
        _tenantAccessor = tenantAccessor;
        _tenantService = tenantService;
        _dbContext = dbContext;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generate registration options for a new passkey credential
    /// </summary>
    [HttpPost("register/options")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> RegisterOptions([FromBody] PasskeyRegisterOptionsRequest request)
    {
        if (string.IsNullOrEmpty(request.Username))
        {
            return Problem(detail: "Username is required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;
        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            request.SubjectId, request.Username, tenantId);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration with attestation response
    /// </summary>
    [HttpPost("register/complete")]
    [AllowAnonymous]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(typeof(PasskeyRegisterCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyRegisterCompleteResponse>> RegisterComplete(
        [FromBody] PasskeyRegisterCompleteRequest request)
    {
        if (string.IsNullOrEmpty(request.ChallengeToken))
        {
            return Problem(detail: "Challenge token not found or expired", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        try
        {
            var result = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenantId);

            return Ok(new PasskeyRegisterCompleteResponse
            {
                CredentialId = result.CredentialId,
                SubjectId = result.SubjectId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passkey registration completion failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Bad Request");
        }
    }

    /// <summary>
    /// Generate discoverable assertion options (no username required)
    /// </summary>
    [HttpPost("login/discoverable/options")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PasskeyOptionsResponse>> DiscoverableLoginOptions()
    {
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
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> LoginOptions([FromBody] PasskeyLoginOptionsRequest request)
    {
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

            var session = await _sessionService.IssueSessionAsync(
                assertionResult.SubjectId,
                new SessionContext(
                    DeviceDescription: "Passkey",
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: Request.Headers.UserAgent.ToString()));

            Response.SetSessionCookies(session, _oidcOptions);

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

        // Look up subject by username within the current tenant
        var subjectEntity = await _dbContext.TenantMembers
            .AsNoTracking()
            .Where(tm => tm.TenantId == tenantId)
            .Select(tm => tm.Subject)
            .FirstOrDefaultAsync(s => s != null && s.Username == request.Username);

        if (subjectEntity == null)
        {
            // Don't reveal whether the username exists
            return Problem(detail: "Invalid username or recovery code", statusCode: 400, title: "Bad Request");
        }

        var verified = await _recoveryCodeService.VerifyAndConsumeAsync(subjectEntity.Id, request.Code);
        if (!verified)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subjectEntity.Id, success: false,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { method = "recovery_code" }));
            return Problem(detail: "Invalid username or recovery code", statusCode: 400, title: "Bad Request");
        }

        await _auditService.LogAsync(AuthAuditEventType.Login, subjectEntity.Id, success: true,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { method = "recovery_code" }));

        // Issue a restricted recovery session (short-lived)
        var subjectInfo = new SubjectInfo
        {
            Id = subjectEntity.Id,
            Name = subjectEntity.Name,
            Email = subjectEntity.Email,
        };

        var recoveryToken = _jwtService.GenerateAccessToken(
            subjectInfo,
            permissions: ["passkey:manage"],
            roles: [],
            lifetime: TimeSpan.FromMinutes(10));

        Response.Cookies.Append(RecoveryCookieName, recoveryToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/",
            IsEssential = true,
        });

        return Ok(new RecoveryVerifyResponse
        {
            Success = true,
            RemainingCodes = await _recoveryCodeService.GetRemainingCountAsync(subjectEntity.Id),
        });
    }

    /// <summary>
    /// List all passkey credentials for the authenticated user
    /// </summary>
    [HttpGet("credentials")]
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

        var tenantId = _tenantAccessor.TenantId;
        var credentials = await _passkeyService.GetCredentialsAsync(auth.SubjectId.Value, tenantId);
        var primaryFactorCount = await _subjectService.CountPrimaryAuthFactorsAsync(auth.SubjectId.Value);

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
        });
    }

    /// <summary>
    /// Remove a passkey credential. Cannot remove the last credential if user has no OIDC link.
    /// </summary>
    [HttpDelete("credentials/{id:guid}")]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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
        return result switch
        {
            FactorRemovalResult.Removed => NoContent(),
            FactorRemovalResult.NotFound => Problem(detail: "Credential not found", statusCode: 404, title: "Not Found"),
            FactorRemovalResult.LastPrimaryFactor => Conflict(new
            {
                error = "last_factor",
                message = "Cannot remove your only remaining sign-in method",
            }),
            _ => throw new InvalidOperationException($"Unexpected FactorRemovalResult: {result}"),
        };
    }

    /// <summary>
    /// Regenerate recovery codes for the authenticated user. Invalidates all existing codes.
    /// </summary>
    [HttpPost("recovery/regenerate")]
    [RemoteCommand(Invalidates = ["GetRecoveryStatus"])]
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

        return Ok(new RecoveryStatusResponse
        {
            RemainingCodes = remaining,
            HasCodes = hasCodes,
            TotalCodes = 8,
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
        var tenantId = _tenantAccessor.TenantId;

        var hasCredentials = await _dbContext.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                _dbContext.SubjectOidcIdentities.Any(o => o.SubjectId == m.SubjectId));
        var setupRequired = !hasCredentials;

        bool recoveryMode;
        if (hasCredentials)
        {
            recoveryMode = await _dbContext.TenantMembers
                .Where(tm => tm.TenantId == tenantId)
                .Join(
                    _dbContext.Subjects.Where(s => s.IsActive && !s.IsSystemSubject),
                    tm => tm.SubjectId,
                    s => s.Id,
                    (tm, s) => s)
                .Where(s =>
                    !_dbContext.SubjectOidcIdentities.Any(i => i.SubjectId == s.Id) &&
                    !_dbContext.PasskeyCredentials.Any(p => p.SubjectId == s.Id))
                .AnyAsync();
        }
        else
        {
            recoveryMode = false;
        }

        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);

        return Ok(new AuthStatusResponse
        {
            SetupRequired = setupRequired,
            RecoveryMode = recoveryMode,
            AllowAccessRequests = tenant?.AllowAccessRequests ?? false,
            OnboardingCompleted = tenant?.OnboardingCompletedAt != null,
        });
    }

    /// <summary>
    /// Mark the current tenant's onboarding as complete.
    /// </summary>
    [HttpPost("onboarding/complete")]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetAuthStatus"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CompleteOnboarding()
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
            return NotFound();

        if (tenant.OnboardingCompletedAt == null)
        {
            tenant.OnboardingCompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        return NoContent();
    }

    /// <summary>
    /// Generate registration options for the first user during initial setup.
    /// Only available when no non-system subjects exist (setup mode).
    /// Creates the subject, assigns admin role, and returns passkey registration options.
    /// </summary>
    [HttpPost("setup/options")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> SetupOptions(
        [FromBody] SetupOptionsRequest request)
    {
        var tenantId = _tenantAccessor.TenantId;

        // Check whether any tenant member already has a passkey credential
        var tenantHasPasskeys = await _dbContext.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m => _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
        if (tenantHasPasskeys)
        {
            return Problem(detail: "Setup mode is not active", statusCode: 403, title: "Forbidden");
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(detail: "Username and display name are required", statusCode: 400, title: "Bad Request");
        }

        // Idempotent: reuse existing setup subject if the WebAuthn ceremony
        // failed on a previous attempt (e.g. user scanned QR with phone on localhost)
        var existingSubject = await _dbContext.Subjects
            .FirstOrDefaultAsync(s => !s.IsSystemSubject && s.IsActive);

        Guid subjectId;
        if (existingSubject != null)
        {
            subjectId = existingSubject.Id;
            // Update in case the user changed their details between attempts
            existingSubject.Name = request.DisplayName.Trim();
            existingSubject.Username = request.Username.Trim().ToLowerInvariant();
            await _dbContext.SaveChangesAsync();

            // Ensure the subject is a member of the current tenant.
            // When a tenant is deleted and recreated, the subject persists but
            // the TenantMember is cascade-deleted with the old tenant.
            var isMember = await _dbContext.TenantMembers
                .AnyAsync(tm => tm.TenantId == tenantId && tm.SubjectId == subjectId);
            if (!isMember)
            {
                var ownerRole = await _dbContext.TenantRoles
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Slug == "owner");
                if (ownerRole != null)
                {
                    await _tenantService.AddMemberAsync(tenantId, subjectId, [ownerRole.Id]);
                }
                await _subjectService.AssignRoleAsync(subjectId, "admin");
            }
        }
        else
        {
            subjectId = Guid.CreateVersion7();
            _dbContext.Subjects.Add(new Infrastructure.Data.Entities.SubjectEntity
            {
                Id = subjectId,
                Name = request.DisplayName.Trim(),
                Username = request.Username.Trim().ToLowerInvariant(),
                IsActive = true,
                IsSystemSubject = false,
            });

            await _dbContext.SaveChangesAsync();

            // Add as owner of the default tenant (seeds roles if needed and assigns owner)
            var ownerRole = await _dbContext.TenantRoles
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Slug == "owner");

            if (ownerRole != null)
            {
                await _tenantService.AddMemberAsync(tenantId, subjectId, [ownerRole.Id]);
            }

            // Assign admin role
            await _subjectService.AssignRoleAsync(subjectId, "admin");

            _logger.LogInformation(
                "Setup: created first user {SubjectId} ({Username}) in tenant {TenantId}",
                subjectId, request.Username.Trim(), tenantId);
        }

        // Generate passkey registration options for the new subject
        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId, request.Username.Trim(), tenantId);

        return Ok(new PasskeyOptionsResponse
        {
            Options = result.OptionsJson,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Complete passkey registration during initial setup.
    /// Verifies attestation, generates recovery codes, issues a full JWT session,
    /// and exits setup mode.
    /// </summary>
    [HttpPost("setup/complete")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SetupCompleteResponse>> SetupComplete(
        [FromBody] SetupCompleteRequest request)
    {
        var tenantId = _tenantAccessor.TenantId;

        // Check whether any tenant member already has a passkey credential
        var tenantHasPasskeys = await _dbContext.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m => _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
        if (tenantHasPasskeys)
        {
            return Problem(detail: "Setup mode is not active", statusCode: 403, title: "Forbidden");
        }

        if (string.IsNullOrEmpty(request.ChallengeToken))
        {
            return Problem(detail: "Challenge token is required", statusCode: 400, title: "Bad Request");
        }

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenantId);

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
                "Setup complete: first user {SubjectId} registered with passkey",
                credResult.SubjectId);

            return Ok(new SetupCompleteResponse
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
            _logger.LogWarning(ex, "Setup passkey registration failed");
            return Problem(detail: "Passkey registration failed during setup", statusCode: 400, title: "Registration Failed");
        }
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
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PasskeyOptionsResponse>> AccessRequestOptions(
        [FromBody] AccessRequestOptionsRequest request)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.AllowAccessRequests)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Problem(detail: "Display name is required", statusCode: 400, title: "Bad Request");

        var displayName = request.DisplayName.Trim();

        var existingPending = await _dbContext.Subjects
            .AnyAsync(s => s.ApprovalStatus == "Pending" && s.Name == displayName);

        if (existingPending)
            return Conflict(new ProblemDetails
            {
                Detail = "A pending access request with this name already exists",
                Status = 409,
                Title = "Conflict",
            });

        var subjectId = Guid.CreateVersion7();
        var username = displayName.ToLowerInvariant().Replace(" ", "-");

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = displayName,
            Username = username,
            IsActive = false,
            IsSystemSubject = false,
            ApprovalStatus = "Pending",
            AccessRequestMessage = request.Message?.Trim(),
        });

        await _dbContext.SaveChangesAsync();

        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId, username, tenant.Id);

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
    /// <param name="request">The attestation response and challenge token from the WebAuthn ceremony.</param>
    /// <param name="notificationService">Injected notification service for alerting owners.</param>
    /// <returns><c>200 OK</c> on success, or <c>400</c> / <c>404</c> on error.</returns>
    [HttpPost("access-request/complete")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AccessRequestComplete(
        [FromBody] AccessRequestCompleteRequest request,
        [FromServices] IInAppNotificationService notificationService)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.AllowAccessRequests)
            return NotFound();

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenant.Id);

            var subject = await _dbContext.Subjects
                .FirstOrDefaultAsync(s => s.Id == credResult.SubjectId);

            var displayName = subject?.Name ?? "Unknown";
            var message = subject?.AccessRequestMessage;

            var ownerIds = await _dbContext.TenantMembers
                .Where(tm => tm.TenantId == tenant.Id
                    && tm.MemberRoles.Any(mr => mr.TenantRole.Slug == Core.Models.Authorization.TenantPermissions.SeedRoles.Owner))
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
    [RemoteCommand]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PasskeyOptionsResponse>> InviteOptions(
        [FromBody] InviteOptionsRequest request,
        [FromServices] IMemberInviteService memberInviteService)
    {
        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(detail: "Token, username, and display name are required", statusCode: 400, title: "Bad Request");
        }

        // Validate the invite
        var invite = await memberInviteService.GetInviteByTokenAsync(request.Token);
        if (invite == null || !invite.IsValid)
            return NotFound();

        var tenantId = _tenantAccessor.TenantId;

        // Create the subject
        var subjectId = Guid.CreateVersion7();
        var username = request.Username.Trim().ToLowerInvariant();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = request.DisplayName.Trim(),
            Username = username,
            IsActive = true,
            IsSystemSubject = false,
        });

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Invite: created subject {SubjectId} ({Username}) for invite acceptance",
            subjectId, username);

        // Generate passkey registration options
        var result = await _passkeyService.GenerateRegistrationOptionsAsync(
            subjectId, username, tenantId);

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
    [HttpPost("invite/complete")]
    [AllowAnonymous]
    [RemoteCommand]
    [ProducesResponseType(typeof(SetupCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SetupCompleteResponse>> InviteComplete(
        [FromBody] InviteCompleteRequest request,
        [FromServices] IMemberInviteService memberInviteService)
    {
        if (string.IsNullOrEmpty(request.ChallengeToken) || string.IsNullOrEmpty(request.Token))
        {
            return Problem(detail: "Challenge token and invite token are required", statusCode: 400, title: "Bad Request");
        }

        var tenantId = _tenantAccessor.TenantId;

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenantId);

            // Accept the invite
            var acceptResult = await memberInviteService.AcceptInviteAsync(request.Token, credResult.SubjectId);
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

            return Ok(new SetupCompleteResponse
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
/// Request for passkey registration options
/// </summary>
public class PasskeyRegisterOptionsRequest
{
    public Guid SubjectId { get; set; }
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
/// Response for completed passkey login
/// </summary>
public class PasskeyLoginCompleteResponse
{
    public bool Success { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
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
/// Request for initial setup registration options (first user creation)
/// </summary>
public class SetupOptionsRequest
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete initial setup registration
/// </summary>
public class SetupCompleteRequest
{
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

/// <summary>
/// Response for completed setup registration
/// </summary>
public class SetupCompleteResponse
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

public class AccessRequestCompleteRequest
{
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

public class InviteOptionsRequest
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class InviteCompleteRequest
{
    public string Token { get; set; } = string.Empty;
    public string AttestationResponseJson { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
}

#endregion
