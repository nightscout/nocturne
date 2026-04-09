using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for WebAuthn/FIDO2 passkey authentication ceremonies.
/// Handles registration, login (both discoverable and non-discoverable), and recovery code verification.
/// </summary>
[ApiController]
[Route("api/auth/passkey")]
[Tags("Passkey")]
[AllowDuringSetup]
public class PasskeyController : ControllerBase
{
    private const string RecoveryCookieName = ".Nocturne.RecoverySession";

    private readonly IPasskeyService _passkeyService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenService _refreshTokenService;
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
        IRefreshTokenService refreshTokenService,
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
        _refreshTokenService = refreshTokenService;
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

            // Get subject details for token generation
            var subject = await _subjectService.GetSubjectByIdAsync(assertionResult.SubjectId);
            if (subject == null)
            {
                return Problem(detail: "User account not found", statusCode: 400, title: "Bad Request");
            }

            var roles = await _subjectService.GetSubjectRolesAsync(assertionResult.SubjectId);
            var permissions = await _subjectService.GetSubjectPermissionsAsync(assertionResult.SubjectId);

            // Generate tokens
            var subjectInfo = new SubjectInfo
            {
                Id = subject.Id,
                Name = assertionResult.DisplayName ?? assertionResult.Username,
                Email = subject.Email,
            };

            var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
            var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
                assertionResult.SubjectId,
                oidcSessionId: null,
                deviceDescription: "Passkey",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());

            SetSessionCookies(accessToken, refreshToken);

            await _auditService.LogAsync(AuthAuditEventType.Login, assertionResult.SubjectId, success: true,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detailsJson: JsonSerializer.Serialize(new { method = "passkey" }));

            return Ok(new PasskeyLoginCompleteResponse
            {
                Success = true,
                AccessToken = accessToken,
                ExpiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds,
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
    /// Returns whether the current tenant is in recovery mode.
    /// In multi-tenant mode, queries the database for orphaned subjects.
    /// In single-tenant mode, reads from the global RecoveryModeState.
    /// </summary>
    [HttpGet("recovery-mode-status")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecoveryModeStatus([FromServices] RecoveryModeState state)
    {
        bool recoveryMode;
        if (_tenantAccessor.IsResolved)
        {
            var tenantId = _tenantAccessor.TenantId;
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
            recoveryMode = state.IsEnabled;
        }

        return Ok(new { recoveryMode });
    }

    /// <summary>
    /// Returns tenant auth status: whether setup is required or recovery mode is active.
    /// In multi-tenant mode, queries the database. In single-tenant mode, reads global state.
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(AuthStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthStatus([FromServices] RecoveryModeState state)
    {
        bool setupRequired;
        bool recoveryMode;

        if (_tenantAccessor.IsResolved)
        {
            var tenantId = _tenantAccessor.TenantId;
            var hasCredentials = await _dbContext.TenantMembers
                .Where(m => m.TenantId == tenantId)
                .AnyAsync(m => _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
            setupRequired = !hasCredentials;

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
        }
        else
        {
            setupRequired = state.IsSetupRequired;
            recoveryMode = state.IsEnabled;
        }

        var tenant = _tenantAccessor.IsResolved
            ? await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantAccessor.TenantId)
            : await _dbContext.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.IsDefault);

        return Ok(new AuthStatusResponse
        {
            SetupRequired = setupRequired,
            RecoveryMode = recoveryMode,
            AllowAccessRequests = tenant?.AllowAccessRequests ?? false,
        });
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
        [FromBody] SetupOptionsRequest request,
        [FromServices] RecoveryModeState state)
    {
        // Use the resolved tenant (multi-tenant) or fall back to the default tenant (single-tenant)
        var tenantId = _tenantAccessor.IsResolved
            ? _tenantAccessor.TenantId
            : (await _dbContext.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.IsDefault))?.Id
                ?? Guid.Empty;

        if (tenantId == Guid.Empty)
        {
            return Problem(detail: "Tenant not found — restart the application", statusCode: 500, title: "Server Error");
        }

        // Single-tenant: RecoveryModeState.IsSetupRequired is set at startup
        // Multi-tenant: no tenant members have passkey credentials yet
        var tenantHasPasskeys = _tenantAccessor.IsResolved &&
            await _dbContext.TenantMembers
                .Where(m => m.TenantId == tenantId)
                .AnyAsync(m => _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
        var tenantNeedsSetup = state.IsSetupRequired ||
            (_tenantAccessor.IsResolved && !tenantHasPasskeys);
        if (!tenantNeedsSetup)
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
        [FromBody] SetupCompleteRequest request,
        [FromServices] RecoveryModeState state)
    {
        var tenantId = _tenantAccessor.IsResolved
            ? _tenantAccessor.TenantId
            : (await _dbContext.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.IsDefault))?.Id
                ?? Guid.Empty;

        if (tenantId == Guid.Empty)
        {
            return Problem(detail: "Tenant not found", statusCode: 500, title: "Server Error");
        }

        var tenantHasPasskeys = _tenantAccessor.IsResolved &&
            await _dbContext.TenantMembers
                .Where(m => m.TenantId == tenantId)
                .AnyAsync(m => _dbContext.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
        var tenantNeedsSetup = state.IsSetupRequired ||
            (_tenantAccessor.IsResolved && !tenantHasPasskeys);
        if (!tenantNeedsSetup)
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

            // Get subject details for token generation
            var subject = await _subjectService.GetSubjectByIdAsync(credResult.SubjectId);
            if (subject == null)
            {
                return Problem(detail: "Created subject not found", statusCode: 500, title: "Server Error");
            }

            var roles = await _subjectService.GetSubjectRolesAsync(credResult.SubjectId);
            var permissions = await _subjectService.GetSubjectPermissionsAsync(credResult.SubjectId);

            // Issue session
            var subjectInfo = new SubjectInfo
            {
                Id = subject.Id,
                Name = subject.Name,
                Email = subject.Email,
            };

            var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
            var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
                credResult.SubjectId,
                oidcSessionId: null,
                deviceDescription: "Setup Passkey",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());

            SetSessionCookies(accessToken, refreshToken);

            // Exit setup mode
            state.IsSetupRequired = false;

            _logger.LogInformation(
                "Setup complete: first user {SubjectId} registered with passkey",
                credResult.SubjectId);

            return Ok(new SetupCompleteResponse
            {
                Success = true,
                RecoveryCodes = recoveryCodes,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup passkey registration failed");
            return Problem(detail: "Passkey registration failed during setup", statusCode: 400, title: "Registration Failed");
        }
    }

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
        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IsDefault);

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
        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IsDefault);

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
                    InAppNotificationType.AnonymousLoginRequest,
                    NotificationUrgency.Info,
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

        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IsDefault);

        if (tenant == null)
            return Problem(detail: "Default tenant not found", statusCode: 500, title: "Server Error");

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
            subjectId, username, tenant.Id);

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

        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IsDefault);

        if (tenant == null)
            return Problem(detail: "Default tenant not found", statusCode: 500, title: "Server Error");

        try
        {
            var credResult = await _passkeyService.CompleteRegistrationAsync(
                request.AttestationResponseJson, request.ChallengeToken, tenant.Id);

            // Accept the invite
            var acceptResult = await memberInviteService.AcceptInviteAsync(request.Token, credResult.SubjectId);
            if (!acceptResult.Success)
            {
                return Problem(detail: acceptResult.ErrorDescription ?? "Failed to accept invite", statusCode: 400, title: "Invite Error");
            }

            // Generate recovery codes
            var recoveryCodes = await _recoveryCodeService.GenerateCodesAsync(credResult.SubjectId);

            // Get subject details for token generation
            var subject = await _subjectService.GetSubjectByIdAsync(credResult.SubjectId);
            if (subject == null)
                return Problem(detail: "Created subject not found", statusCode: 500, title: "Server Error");

            var roles = await _subjectService.GetSubjectRolesAsync(credResult.SubjectId);
            var permissions = await _subjectService.GetSubjectPermissionsAsync(credResult.SubjectId);

            // Issue session
            var subjectInfo = new SubjectInfo
            {
                Id = subject.Id,
                Name = subject.Name,
                Email = subject.Email,
            };

            var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
            var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
                credResult.SubjectId,
                oidcSessionId: null,
                deviceDescription: "Invite Passkey",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());

            SetSessionCookies(accessToken, refreshToken);

            _logger.LogInformation(
                "Invite complete: subject {SubjectId} registered with passkey via invite",
                credResult.SubjectId);

            return Ok(new SetupCompleteResponse
            {
                Success = true,
                RecoveryCodes = recoveryCodes,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invite passkey registration failed");
            return Problem(detail: "Passkey registration failed", statusCode: 400, title: "Registration Failed");
        }
    }

    #region Private Helpers

    private void SetSessionCookies(string accessToken, string refreshToken)
    {
        var cookieSameSite = _oidcOptions.Cookie.SameSite switch
        {
            SameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            SameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            SameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
            _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        };

        Response.Cookies.Append(_oidcOptions.Cookie.AccessTokenName, accessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = _oidcOptions.Cookie.Secure,
            SameSite = cookieSameSite,
            Path = "/",
            IsEssential = true,
            MaxAge = _jwtService.GetAccessTokenLifetime(),
        });

        Response.Cookies.Append(_oidcOptions.Cookie.RefreshTokenName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = _oidcOptions.Cookie.Secure,
            SameSite = cookieSameSite,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(7),
        });

        // IsAuthenticated is intentionally not HttpOnly — the frontend reads it to detect auth state.
        // The actual tokens (access + refresh) are HttpOnly above. This cookie contains no secrets.
        Response.Cookies.Append("IsAuthenticated", "true", new CookieOptions // lgtm[cs/web/cookie-httponly-not-set]
        {
            HttpOnly = false,
            Secure = _oidcOptions.Cookie.Secure,
            SameSite = cookieSameSite,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7),
        });
    }

    #endregion
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
