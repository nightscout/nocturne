using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for TOTP (Time-based One-Time Password) authenticator management and login.
/// Handles setup, verification, credential listing/removal, and TOTP-based authentication.
/// </summary>
[ApiController]
[Route("api/auth/totp")]
[Tags("Totp")]
[AllowDuringSetup]
public class TotpController : ControllerBase
{
    private readonly ITotpService _totpService;
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ISubjectService _subjectService;
    private readonly IAuthAuditService _auditService;
    private readonly OidcOptions _oidcOptions;
    private readonly ILogger<TotpController> _logger;

    /// <summary>
    /// Creates a new instance of TotpController
    /// </summary>
    public TotpController(
        ITotpService totpService,
        IJwtService jwtService,
        IRefreshTokenService refreshTokenService,
        ISubjectService subjectService,
        IAuthAuditService auditService,
        IOptions<OidcOptions> oidcOptions,
        ILogger<TotpController> logger)
    {
        _totpService = totpService;
        _jwtService = jwtService;
        _refreshTokenService = refreshTokenService;
        _subjectService = subjectService;
        _auditService = auditService;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generate TOTP setup data including provisioning URI and secret
    /// </summary>
    [HttpPost("setup")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TotpSetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TotpSetupResponse>> Setup()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        var subject = await _subjectService.GetSubjectByIdAsync(auth.SubjectId.Value);
        if (subject == null)
            return Problem(detail: "User account not found", statusCode: 400, title: "Bad Request");

        // TOTP is a second factor; require at least one primary factor (passkey or OIDC link)
        // so a user cannot end up with only TOTP configured.
        var primaryFactorCount = await _subjectService.CountPrimaryAuthFactorsAsync(auth.SubjectId.Value);
        if (primaryFactorCount < 1)
        {
            return BadRequest(new
            {
                error = "no_primary_factor",
                message = "Configure a passkey or linked sign-in method before enabling TOTP",
            });
        }

        var result = await _totpService.GenerateSetupAsync(auth.SubjectId.Value, subject.Name);

        return Ok(new TotpSetupResponse
        {
            ProvisioningUri = result.ProvisioningUri,
            Base32Secret = result.Base32Secret,
            ChallengeToken = result.ChallengeToken,
        });
    }

    /// <summary>
    /// Verify a TOTP code to complete authenticator setup
    /// </summary>
    [HttpPost("verify-setup")]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
    [ProducesResponseType(typeof(TotpVerifySetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TotpVerifySetupResponse>> VerifySetup([FromBody] TotpVerifySetupRequest request)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");

        try
        {
            var result = await _totpService.CompleteSetupAsync(request.Code, request.Label, request.ChallengeToken);

            return Ok(new TotpVerifySetupResponse
            {
                CredentialId = result.CredentialId,
                Success = true,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    /// <summary>
    /// List all TOTP credentials for the authenticated user
    /// </summary>
    [HttpGet]
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
    /// Remove a TOTP credential by ID
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["ListCredentials"])]
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
        await _totpService.RemoveCredentialAsync(id, auth.SubjectId.Value);

        return NoContent();
    }

    /// <summary>
    /// Authenticate using a TOTP code and username
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("totp-login")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TotpLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TotpLoginResponse>> Login([FromBody] TotpLoginRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();

        var result = await _totpService.VerifyLoginAsync(request.Username, request.Code);
        if (result == null)
        {
            await _auditService.LogAsync(AuthAuditEventType.FailedAuth, subjectId: null, success: false,
                ipAddress: ip, userAgent: ua,
                detailsJson: JsonSerializer.Serialize(new { method = "totp", username = request.Username }));
            return Problem(detail: "Invalid username or code", statusCode: 400, title: "Bad Request");
        }

        var subject = await _subjectService.GetSubjectByIdAsync(result.SubjectId);
        if (subject == null)
        {
            return Problem(detail: "User account not found", statusCode: 400, title: "Bad Request");
        }

        var roles = await _subjectService.GetSubjectRolesAsync(result.SubjectId);
        var permissions = await _subjectService.GetSubjectPermissionsAsync(result.SubjectId);

        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = result.DisplayName ?? result.Username,
            Email = subject.Email,
        };

        var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
            result.SubjectId,
            oidcSessionId: null,
            deviceDescription: "TOTP",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString());

        SetSessionCookies(accessToken, refreshToken);

        await _subjectService.UpdateLastLoginAsync(result.SubjectId);

        await _auditService.LogAsync(AuthAuditEventType.Login, result.SubjectId, success: true,
            ipAddress: ip, userAgent: ua,
            detailsJson: JsonSerializer.Serialize(new { method = "totp" }));

        return Ok(new TotpLoginResponse
        {
            Success = true,
            AccessToken = accessToken,
            ExpiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds,
        });
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
/// Request to authenticate using TOTP
/// </summary>
public class TotpLoginRequest
{
    [Required, StringLength(255)]
    public string Username { get; set; } = string.Empty;

    [Required, RegularExpression(@"^\d{6}$")]
    public string Code { get; set; } = string.Empty;
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
