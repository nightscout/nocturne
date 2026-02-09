using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;
using SameSiteMode = Nocturne.Core.Models.Configuration.SameSiteMode;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for the built-in local identity provider
/// Handles registration, login, password management, and email verification
/// </summary>
[ApiController]
[Route("auth/local")]
[Tags("LocalAuth")]
public class LocalAuthController : ControllerBase
{
    private readonly ILocalIdentityService _identityService;
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ISubjectService _subjectService;
    private readonly LocalIdentityOptions _options;
    private readonly OidcOptions _oidcOptions;
    private readonly ILogger<LocalAuthController> _logger;

    /// <summary>
    /// Creates a new instance of LocalAuthController
    /// </summary>
    public LocalAuthController(
        ILocalIdentityService identityService,
        IJwtService jwtService,
        IRefreshTokenService refreshTokenService,
        ISubjectService subjectService,
        IOptions<LocalIdentityOptions> options,
        IOptions<OidcOptions> oidcOptions,
        ILogger<LocalAuthController> logger
    )
    {
        _identityService = identityService;
        _jwtService = jwtService;
        _refreshTokenService = refreshTokenService;
        _subjectService = subjectService;
        _options = options.Value;
        _oidcOptions = oidcOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get local identity provider configuration
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalAuthConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<LocalAuthConfigResponse> GetConfig()
    {
        return Ok(
            new LocalAuthConfigResponse
            {
                Enabled = _options.Enabled,
                DisplayName = _options.DisplayName,
                AllowRegistration = _options.Registration.AllowRegistration,
                RequireEmailVerification = _options.Registration.RequireEmailVerification,
                PasswordRequirements = new PasswordRequirementsDto
                {
                    MinLength = _options.Password.MinLength,
                    RequireUppercase = _options.Password.RequireUppercase,
                    RequireLowercase = _options.Password.RequireLowercase,
                    RequireDigit = _options.Password.RequireDigit,
                    RequireSpecialCharacter = _options.Password.RequireSpecialCharacter,
                },
            }
        );
    }

    /// <summary>
    /// Register a new user account
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/local/register")]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest request)
    {
        if (!_options.Enabled)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "local_auth_disabled",
                    Message = "Local authentication is not enabled",
                }
            );
        }

        if (!_options.Registration.AllowRegistration)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "registration_disabled",
                    Message = "Registration is not currently available",
                }
            );
        }

        try
        {
            var result = await _identityService.RegisterAsync(
                new LocalRegistrationRequest
                {
                    Email = request.Email,
                    Password = request.Password,
                    DisplayName = request.DisplayName,
                }
            );

            if (!result.Success)
            {
                return BadRequest(
                    new ErrorResponse
                    {
                        Error = result.Error ?? "registration_failed",
                        Message = result.ErrorMessage ?? "Registration failed",
                    }
                );
            }

            return Ok(
                new RegisterResponse
                {
                    Success = true,
                    UserId = result.User!.Id,
                    RequiresEmailVerification = result.RequiresEmailVerification,
                    RequiresAdminApproval = result.RequiresAdminApproval,
                    Message = GetRegistrationMessage(result),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed unexpectedly for {Email}", request.Email);
            return StatusCode(
                500,
                new ErrorResponse
                {
                    Error = "registration_error",
                    Message = "An unexpected error occurred during registration. Please try again.",
                }
            );
        }
    }

    /// <summary>
    /// Log in with email and password
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/local/login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        if (!_options.Enabled)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "local_auth_disabled",
                    Message = "Local authentication is not enabled",
                }
            );
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _identityService.AuthenticateAsync(
            request.Email,
            request.Password,
            ipAddress
        );

        if (!result.Success)
        {
            _logger.LogWarning("Login failed for user {Email}. Error: {Error}, Message: {Message}", request.Email, result.ErrorCode, result.ErrorMessage);
            var response = new ErrorResponse
            {
                Error = result.ErrorCode ?? "auth_failed",
                Message = result.ErrorMessage ?? "Authentication failed",
            };

            if (result.RemainingAttempts.HasValue)
            {
                response.Details = new { remainingAttempts = result.RemainingAttempts.Value };
            }

            if (result.LockedUntil.HasValue)
            {
                response.Details = new { lockedUntil = result.LockedUntil.Value };
            }

            return Unauthorized(response);
        }

        // Get subject with roles and permissions
        var subject = await _subjectService.GetSubjectByIdAsync(result.SubjectId!.Value);
        if (subject == null)
        {
            return StatusCode(
                500,
                new ErrorResponse
                {
                    Error = "internal_error",
                    Message = "Failed to retrieve user subject",
                }
            );
        }

        var roles = await _subjectService.GetSubjectRolesAsync(result.SubjectId!.Value);
        var permissions = await _subjectService.GetSubjectPermissionsAsync(result.SubjectId!.Value);

        // Generate tokens
        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = result.User!.DisplayName ?? result.User.Email,
            Email = result.User.Email,
            OidcSubjectId = result.User.Id.ToString(),
            OidcIssuer = "local",
        };

        var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
            result.SubjectId!.Value,
            oidcSessionId: null,
            deviceDescription: null,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString()
        );

        // Set session cookies
        SetAuthCookies(accessToken, refreshToken);

        return Ok(
            new LoginResponse
            {
                Success = true,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds,
                User = new UserInfoDto
                {
                    Id = result.User.Id,
                    Email = result.User.Email,
                    DisplayName = result.User.DisplayName,
                    Roles = roles,
                },
                RequirePasswordChange = result.RequirePasswordChange,
            }
        );
    }

    /// <summary>
    /// Verify email address
    /// </summary>
    [HttpGet("verify-email")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/local/verify-email")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "invalid_token",
                    Message = "Verification token is required",
                }
            );
        }

        var success = await _identityService.VerifyEmailAsync(token);

        if (success)
        {
            // Redirect to login page with success message
            var returnUrl = _oidcOptions.DefaultReturnUrl ?? "/";
            return Redirect(
                $"/auth/login?verified=true&returnUrl={Uri.EscapeDataString(returnUrl)}"
            );
        }

        return Redirect(
            "/auth/error?error=invalid_token&message=Invalid or expired verification link"
        );
    }

    /// <summary>
    /// Request password reset
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/local/forgot-password")]
    [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword(
        [FromBody] ForgotPasswordRequest request
    )
    {
        if (!_options.Enabled)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "local_auth_disabled",
                    Message = "Local authentication is not enabled",
                }
            );
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _identityService.RequestPasswordResetAsync(
            request.Email,
            ipAddress,
            userAgent
        );

        // Always return success to prevent email enumeration
        return Ok(
            new ForgotPasswordResponse
            {
                Success = true,
                Message = result.Message,
                AdminNotificationRequired = result.AdminNotificationRequired,
            }
        );
    }

    /// <summary>
    /// Reset password with token
    /// </summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [NightscoutEndpoint("/auth/local/reset-password")]
    [ProducesResponseType(typeof(ResetPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(
        [FromBody] ResetPasswordRequest request
    )
    {
        if (!_options.Enabled)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "local_auth_disabled",
                    Message = "Local authentication is not enabled",
                }
            );
        }

        // Validate password first to give better error messages
        var passwordValidation = _identityService.ValidatePassword(request.NewPassword);
        if (!passwordValidation.IsValid)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "invalid_password",
                    Message = string.Join("; ", passwordValidation.Errors),
                }
            );
        }

        var success = await _identityService.ResetPasswordAsync(request.Token, request.NewPassword);

        if (!success)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "invalid_token",
                    Message = "Invalid or expired reset token",
                }
            );
        }

        return Ok(
            new ResetPasswordResponse
            {
                Success = true,
                Message =
                    "Password has been reset successfully. You can now log in with your new password.",
            }
        );
    }

    /// <summary>
    /// Change password for authenticated user
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [NightscoutEndpoint("/auth/local/change-password")]
    [ProducesResponseType(typeof(ChangePasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ChangePasswordResponse>> ChangePassword(
        [FromBody] ChangePasswordRequest request
    )
    {
        var auth = HttpContext.GetAuthContext();
        if (auth?.SubjectId == null)
        {
            return Unauthorized();
        }

        // Get the local user ID from the subject
        var subject = await _subjectService.GetSubjectByIdAsync(auth.SubjectId.Value);
        if (subject == null || subject.OidcIssuer != "local")
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "not_local_user",
                    Message = "Password change is only available for local accounts",
                }
            );
        }

        // Parse the local user ID from OIDC subject ID
        if (!Guid.TryParse(subject.OidcSubjectId, out var localUserId))
        {
            return BadRequest(
                new ErrorResponse { Error = "invalid_user", Message = "Invalid user account" }
            );
        }

        // Validate new password
        var passwordValidation = _identityService.ValidatePassword(request.NewPassword);
        if (!passwordValidation.IsValid)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "invalid_password",
                    Message = string.Join("; ", passwordValidation.Errors),
                }
            );
        }

        var success = await _identityService.ChangePasswordAsync(
            localUserId,
            request.CurrentPassword,
            request.NewPassword
        );

        if (!success)
        {
            return BadRequest(
                new ErrorResponse
                {
                    Error = "change_failed",
                    Message = "Current password is incorrect",
                }
            );
        }

        return Ok(
            new ChangePasswordResponse { Success = true, Message = "Password changed successfully" }
        );
    }

    /// <summary>
    /// Check if email is allowed to register (for form validation)
    /// </summary>
    [HttpGet("check-email")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CheckEmailResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CheckEmailResponse>> CheckEmail([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Ok(new CheckEmailResponse { IsValid = false, Error = "Email is required" });
        }

        var exists = await _identityService.UserExistsAsync(email);
        if (exists)
        {
            return Ok(
                new CheckEmailResponse
                {
                    IsValid = false,
                    Error = "An account with this email already exists",
                }
            );
        }

        var allowlistResult = _identityService.CheckAllowlist(email);
        if (!allowlistResult.IsAllowed)
        {
            return Ok(new CheckEmailResponse { IsValid = false, Error = allowlistResult.Reason });
        }

        return Ok(
            new CheckEmailResponse
            {
                IsValid = true,
                RequiresApproval = allowlistResult.RequiresApproval,
            }
        );
    }

    /// <summary>
    /// Resend email verification
    /// </summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ResendVerificationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResendVerificationResponse>> ResendVerification(
        [FromBody] ResendVerificationRequest request
    )
    {
        // This would need additional implementation to generate a new verification token
        // For now, we just return a generic message
        return Ok(
            new ResendVerificationResponse
            {
                Success = true,
                Message =
                    "If an unverified account exists with this email, a new verification link will be sent.",
            }
        );
    }

    // ============================================================================
    // Admin endpoints
    // ============================================================================

    /// <summary>
    /// Get pending password reset requests (admin only)
    /// </summary>
    [HttpGet("admin/password-resets")]
    [Authorize]
    [RequireAdmin]
    [RemoteQuery]
    [ProducesResponseType(typeof(PasswordResetRequestListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PasswordResetRequestListResponse>> GetPendingPasswordResets()
    {
        var requests = await _identityService.GetPendingPasswordResetRequestsAsync();
        var items = requests
            .Select(r => new PasswordResetRequestDto
            {
                Id = r.Id,
                Email = r.Email,
                DisplayName = r.DisplayName,
                RequestedFromIp = r.RequestedFromIp,
                UserAgent = r.UserAgent,
                CreatedAt = r.CreatedAt,
            })
            .ToList();

        return Ok(new PasswordResetRequestListResponse { Requests = items, TotalCount = items.Count });
    }

    /// <summary>
    /// Set a temporary password for a user (admin only)
    /// </summary>
    [HttpPost("admin/set-temporary-password")]
    [Authorize]
    [RequireAdmin]
    [RemoteCommand(Invalidates = ["GetPendingPasswordResets"])]
    [ProducesResponseType(typeof(SetTemporaryPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SetTemporaryPasswordResponse>> SetTemporaryPassword(
        [FromBody] SetTemporaryPasswordRequest request
    )
    {
        var auth = HttpContext.GetAuthContext();
        if (auth?.SubjectId == null)
        {
            return Unauthorized();
        }

        var user = await _identityService.GetUserByEmailAsync(request.Email);
        if (user == null)
        {
            return BadRequest(new ErrorResponse { Error = "user_not_found", Message = string.Empty });
        }

        var success = await _identityService.SetTemporaryPasswordAsync(
            user.Id,
            request.TemporaryPassword,
            auth.SubjectId.Value
        );

        if (!success)
        {
            return BadRequest(
                new ErrorResponse { Error = "set_password_failed", Message = string.Empty }
            );
        }

        return Ok(new SetTemporaryPasswordResponse { Success = true });
    }

    /// <summary>
    /// Handle a password reset request by generating a reset link (admin only)
    /// </summary>
    [HttpPost("admin/handle-password-reset/{requestId:guid}")]
    [Authorize]
    [RequireAdmin]
    [RemoteCommand(Invalidates = ["GetPendingPasswordResets"])]
    [ProducesResponseType(typeof(HandlePasswordResetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HandlePasswordResetResponse>> HandlePasswordReset(Guid requestId)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth?.SubjectId == null)
        {
            return Unauthorized();
        }

        var resetUrl = await _identityService.HandlePasswordResetRequestAsync(
            requestId,
            auth.SubjectId.Value
        );
        if (resetUrl == null)
        {
            return BadRequest(new ErrorResponse { Error = "handle_failed", Message = string.Empty });
        }

        return Ok(new HandlePasswordResetResponse { Success = true, ResetUrl = resetUrl });
    }

    #region Private Helpers

    private void SetAuthCookies(string accessToken, string refreshToken)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _oidcOptions.Cookie.Secure,
            SameSite = _oidcOptions.Cookie.SameSite switch
            {
                SameSiteMode.Strict => Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                SameSiteMode.Lax => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                SameSiteMode.None => Microsoft.AspNetCore.Http.SameSiteMode.None,
                _ => Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            },
            Path = "/",
            IsEssential = true,
        };

        // Access token cookie
        Response.Cookies.Append(
            _oidcOptions.Cookie.AccessTokenName,
            accessToken,
            new CookieOptions
            {
                HttpOnly = cookieOptions.HttpOnly,
                Secure = cookieOptions.Secure,
                SameSite = cookieOptions.SameSite,
                Path = cookieOptions.Path,
                IsEssential = cookieOptions.IsEssential,
                MaxAge = _jwtService.GetAccessTokenLifetime(),
            }
        );

        // Refresh token cookie
        Response.Cookies.Append(
            _oidcOptions.Cookie.RefreshTokenName,
            refreshToken,
            new CookieOptions
            {
                HttpOnly = cookieOptions.HttpOnly,
                Secure = cookieOptions.Secure,
                SameSite = cookieOptions.SameSite,
                Path = cookieOptions.Path,
                IsEssential = cookieOptions.IsEssential,
                MaxAge = TimeSpan.FromDays(7), // Refresh token lifetime
            }
        );

        // Set a non-HttpOnly cookie for client-side auth state checking
        // Use refresh token lifetime so frontend can detect auth state and trigger refresh
        Response.Cookies.Append(
            "IsAuthenticated",
            "true",
            new CookieOptions
            {
                HttpOnly = false,
                Secure = cookieOptions.Secure,
                SameSite = cookieOptions.SameSite,
                Path = "/",
                MaxAge = TimeSpan.FromDays(7), // Match refresh token lifetime
            }
        );
    }

    private static string GetRegistrationMessage(LocalRegistrationResult result)
    {
        if (result.RequiresEmailVerification && result.RequiresAdminApproval)
        {
            return "Please check your email to verify your address. Your account will also require administrator approval before you can log in.";
        }

        if (result.RequiresEmailVerification)
        {
            return "Please check your email to verify your address before logging in.";
        }

        if (result.RequiresAdminApproval)
        {
            return "Your registration is pending administrator approval. You will be notified when your account is activated.";
        }

        return "Registration successful! You can now log in.";
    }

    #endregion
}

#region Request/Response DTOs

/// <summary>
/// Local auth configuration response
/// </summary>
public class LocalAuthConfigResponse
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool AllowRegistration { get; set; }
    public bool RequireEmailVerification { get; set; }
    public PasswordRequirementsDto PasswordRequirements { get; set; } = new();
}

/// <summary>
/// Password requirements
/// </summary>
public class PasswordRequirementsDto
{
    public int MinLength { get; set; }
    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireDigit { get; set; }
    public bool RequireSpecialCharacter { get; set; }
}

/// <summary>
/// Registration request
/// </summary>
public class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

/// <summary>
/// Registration response
/// </summary>
public class RegisterResponse
{
    public bool Success { get; set; }
    public Guid UserId { get; set; }
    public bool RequiresEmailVerification { get; set; }
    public bool RequiresAdminApproval { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Login request
/// </summary>
public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Login response
/// </summary>
public class LoginResponse
{
    public bool Success { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public UserInfoDto User { get; set; } = new();
    public bool RequirePasswordChange { get; set; }
}

/// <summary>
/// User info
/// </summary>
public class UserInfoDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public List<string> Roles { get; set; } = new();
}

/// <summary>
/// Forgot password request
/// </summary>
public class ForgotPasswordRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Forgot password response
/// </summary>
public class ForgotPasswordResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool AdminNotificationRequired { get; set; }
}

/// <summary>
/// Reset password request
/// </summary>
public class ResetPasswordRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Reset password response
/// </summary>
public class ResetPasswordResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Change password request
/// </summary>
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Change password response
/// </summary>
public class ChangePasswordResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Password reset request info for admin view
/// </summary>
public class PasswordResetRequestDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? RequestedFromIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response for pending password reset requests
/// </summary>
public class PasswordResetRequestListResponse
{
    public List<PasswordResetRequestDto> Requests { get; set; } = new();
    public int TotalCount { get; set; }
}

/// <summary>
/// Request to set a temporary password
/// </summary>
public class SetTemporaryPasswordRequest
{
    public string Email { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
}

/// <summary>
/// Response for setting a temporary password
/// </summary>
public class SetTemporaryPasswordResponse
{
    public bool Success { get; set; }
}

/// <summary>
/// Response for handling password reset
/// </summary>
public class HandlePasswordResetResponse
{
    public bool Success { get; set; }
    public string ResetUrl { get; set; } = string.Empty;
}

/// <summary>
/// Check email response
/// </summary>
public class CheckEmailResponse
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public bool RequiresApproval { get; set; }
}

/// <summary>
/// Resend verification request
/// </summary>
public class ResendVerificationRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Resend verification response
/// </summary>
public class ResendVerificationResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Error response
/// </summary>
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }
}

#endregion
