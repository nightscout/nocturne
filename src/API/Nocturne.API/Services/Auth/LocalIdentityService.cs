using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Implementation of the local identity provider
/// Handles user registration, authentication, and password management
/// </summary>
public class LocalIdentityService : ILocalIdentityService
{
    private readonly NocturneDbContext _dbContext;
    private readonly ISubjectService _subjectService;
    private readonly IEmailService _emailService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly LocalIdentityOptions _options;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<LocalIdentityService> _logger;

    /// <summary>
    /// Creates a new instance of LocalIdentityService
    /// </summary>
    public LocalIdentityService(
        NocturneDbContext dbContext,
        ISubjectService subjectService,
        IEmailService emailService,
        ISignalRBroadcastService signalRBroadcastService,
        IOptions<LocalIdentityOptions> options,
        IOptions<EmailOptions> emailOptions,
        ILogger<LocalIdentityService> logger
    )
    {
        _dbContext = dbContext;
        _subjectService = subjectService;
        _emailService = emailService;
        _signalRBroadcastService = signalRBroadcastService;
        _options = options.Value;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> UserExistsAsync(string email)
    {
        var normalizedEmail = NormalizeEmail(email);
        return await _dbContext.LocalUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail);
    }

    /// <inheritdoc />
    public async Task<LocalRegistrationResult> RegisterAsync(LocalRegistrationRequest request)
    {
        // Validate email format
        if (string.IsNullOrWhiteSpace(request.Email) || !IsValidEmail(request.Email))
        {
            return LocalRegistrationResult.Failed("invalid_email", "Invalid email address format");
        }

        // Validate password
        var passwordValidation = ValidatePassword(request.Password);
        if (!passwordValidation.IsValid)
        {
            return LocalRegistrationResult.Failed(
                "invalid_password",
                string.Join("; ", passwordValidation.Errors)
            );
        }

        // Check if registration is allowed
        if (!_options.Registration.AllowRegistration)
        {
            return LocalRegistrationResult.Failed(
                "registration_disabled",
                "Registration is not currently available"
            );
        }

        // Check if user already exists
        var normalizedEmail = NormalizeEmail(request.Email);
        if (await _dbContext.LocalUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
        {
            return LocalRegistrationResult.Failed(
                "email_exists",
                "An account with this email already exists"
            );
        }

        // Check if this is the first user - if so, auto-promote to admin
        var isFirstUser = !await _dbContext.LocalUsers.AnyAsync();

        // Check allowlist (skip for first user)
        var allowlistResult = CheckAllowlist(request.Email);
        if (
            !isFirstUser
            && !allowlistResult.IsAllowed
            && !_options.Allowlist.AllowOthersWithApproval
        )
        {
            return LocalRegistrationResult.Failed(
                "not_allowed",
                allowlistResult.Reason ?? "Email not allowed to register"
            );
        }

        // First user doesn't need approval; others might
        var requiresApproval =
            !isFirstUser
            && (allowlistResult.RequiresApproval || _options.Registration.RequireAdminApproval);

        // Only require email verification if email is actually enabled AND not the first user
        var requiresVerification =
            !isFirstUser
            && _options.Registration.RequireEmailVerification
            && _emailService.IsEnabled;

        // Create the user (first user is auto-verified and active)
        var passwordHash = HashPassword(request.Password);
        _logger.LogDebug(
            "Registering user {Email}, password length: {PasswordLength}, hash length: {HashLength}",
            request.Email,
            request.Password?.Length ?? 0,
            passwordHash?.Length ?? 0
        );

        var user = new LocalUserEntity
        {
            Id = Guid.CreateVersion7(),
            Email = request.Email,
            NormalizedEmail = normalizedEmail,
            DisplayName = request.DisplayName,
            PasswordHash = passwordHash,
            EmailVerified = isFirstUser || !requiresVerification, // First user or no verification needed
            IsActive = isFirstUser || !requiresApproval, // First user is always active
            PendingApproval = !isFirstUser && requiresApproval,
            PasswordChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Generate email verification token
        string? verificationToken = null;
        if (requiresVerification)
        {
            verificationToken = GenerateSecureToken();
            user.EmailVerificationTokenHash = HashToken(verificationToken);
            user.EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddHours(
                _options.Tokens.EmailVerificationTokenHours
            );
        }

        // Create associated Subject for permissions
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = request.DisplayName ?? request.Email,
            Email = request.Email,
            OidcSubjectId = user.Id.ToString(),
            OidcIssuer = "local",
            IsActive = !requiresApproval,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        user.SubjectId = subject.Id;

        _dbContext.Subjects.Add(subject);
        _dbContext.LocalUsers.Add(user);

        // Assign roles - first user gets admin, others get default roles
        var assignedRoleIds = new HashSet<Guid>();
        if (isFirstUser)
        {
            // First user is automatically admin
            var adminRole = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == "admin");
            if (adminRole != null && assignedRoleIds.Add(adminRole.Id))
            {
                _dbContext.SubjectRoles.Add(
                    new SubjectRoleEntity
                    {
                        SubjectId = subject.Id,
                        RoleId = adminRole.Id,
                        AssignedAt = DateTime.UtcNow,
                    }
                );
            }

            _logger.LogInformation(
                "First user {Email} automatically assigned admin role",
                request.Email
            );
        }
        else
        {
            // Assign default roles for non-first users
            foreach (var roleName in _options.Registration.DefaultRoles
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
                if (role != null && assignedRoleIds.Add(role.Id))
                {
                    _dbContext.SubjectRoles.Add(
                        new SubjectRoleEntity
                        {
                            SubjectId = subject.Id,
                            RoleId = role.Id,
                            AssignedAt = DateTime.UtcNow,
                        }
                    );
                }
            }
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Registered new local user {UserId} ({Email}), RequiresVerification={RequiresVerification}, RequiresApproval={RequiresApproval}",
            user.Id,
            request.Email,
            requiresVerification,
            requiresApproval
        );

        // Send verification email if required and SMTP is configured
        if (requiresVerification && verificationToken != null)
        {
            var baseUrl = _emailOptions.BaseUrl?.TrimEnd('/') ?? "";
            var verificationUrl = $"{baseUrl}/auth/verify-email?token={verificationToken}";
            await _emailService.SendEmailVerificationAsync(
                request.Email,
                request.DisplayName,
                verificationUrl
            );
        }

        // Notify admin if approval is required
        if (requiresApproval)
        {
            await _emailService.SendAdminNewUserNotificationAsync(
                request.Email,
                request.DisplayName
            );
        }

        return LocalRegistrationResult.Succeeded(
            MapToModel(user),
            subject.Id,
            requiresVerification,
            requiresApproval
        );
    }

    /// <inheritdoc />
    public async Task<LocalRegistrationResult> RegisterAsync(
        string email,
        string password,
        string? displayName = null,
        bool skipAllowlistCheck = false,
        bool autoVerifyEmail = false
    )
    {
        // Validate email format
        if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        {
            return LocalRegistrationResult.Failed("invalid_email", "Invalid email address format");
        }

        // Validate password
        var passwordValidation = ValidatePassword(password);
        if (!passwordValidation.IsValid)
        {
            return LocalRegistrationResult.Failed(
                "invalid_password",
                string.Join("; ", passwordValidation.Errors)
            );
        }

        // Check if user already exists
        var normalizedEmail = NormalizeEmail(email);
        if (await _dbContext.LocalUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
        {
            return LocalRegistrationResult.Failed(
                "email_exists",
                "An account with this email already exists"
            );
        }

        // Check allowlist (unless skipping for admin seed)
        var requiresApproval = _options.Registration.RequireAdminApproval;
        if (!skipAllowlistCheck)
        {
            var allowlistResult = CheckAllowlist(email);
            if (!allowlistResult.IsAllowed && !_options.Allowlist.AllowOthersWithApproval)
            {
                return LocalRegistrationResult.Failed(
                    "not_allowed",
                    allowlistResult.Reason ?? "Email not allowed to register"
                );
            }
            requiresApproval = allowlistResult.RequiresApproval || requiresApproval;
        }

        // Create the user
        var user = new LocalUserEntity
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            DisplayName = displayName,
            PasswordHash = HashPassword(password),
            EmailVerified = autoVerifyEmail,
            IsActive = true, // Active for admin seeding
            PendingApproval = false,
            PasswordChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Create associated Subject for permissions
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = displayName ?? email,
            Email = email,
            OidcSubjectId = user.Id.ToString(),
            OidcIssuer = "local",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        user.SubjectId = subject.Id;

        _dbContext.Subjects.Add(subject);
        _dbContext.LocalUsers.Add(user);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Registered new local user {UserId} ({Email}), AutoVerified={AutoVerified}",
            user.Id,
            email,
            autoVerifyEmail
        );

        return LocalRegistrationResult.Succeeded(
            MapToModel(user),
            subject.Id,
            !autoVerifyEmail && _options.Registration.RequireEmailVerification,
            requiresApproval
        );
    }

    /// <inheritdoc />
    public async Task<LocalAuthResult> AuthenticateAsync(
        string email,
        string password,
        string? ipAddress = null
    )
    {
        var normalizedEmail = NormalizeEmail(email);
        var user = await _dbContext
            .LocalUsers.Include(u => u.Subject)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);

        if (user == null)
        {
            _logger.LogDebug("Authentication failed: user not found for email {Email}", email);
            return LocalAuthResult.Failed("invalid_credentials", "Invalid email or password");
        }

        // Check if locked out
        if (user.LockedUntil.HasValue && user.LockedUntil > DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Authentication failed: user {UserId} is locked out until {LockedUntil}",
                user.Id,
                user.LockedUntil
            );
            return LocalAuthResult.Failed(
                "locked_out",
                "Account is temporarily locked. Please try again later.",
                lockedUntil: user.LockedUntil
            );
        }

        // Check if active
        if (!user.IsActive)
        {
            _logger.LogDebug("Authentication failed: user {UserId} is not active", user.Id);
            return LocalAuthResult.Failed("account_inactive", "Account is not active");
        }

        // Check if pending approval
        if (user.PendingApproval)
        {
            _logger.LogDebug("Authentication failed: user {UserId} is pending approval", user.Id);
            return LocalAuthResult.Failed(
                "pending_approval",
                "Account is pending administrator approval"
            );
        }

        // Check if email verified
        if (_options.Registration.RequireEmailVerification && !user.EmailVerified)
        {
            _logger.LogDebug("Authentication failed: user {UserId} email not verified", user.Id);
            return LocalAuthResult.Failed(
                "email_not_verified",
                "Please verify your email address before logging in"
            );
        }

        // Verify password
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            _logger.LogWarning(
                "Authentication failed: user {UserId} has no password hash set",
                user.Id
            );
            await HandleFailedLoginAttempt(user);
            return LocalAuthResult.Failed("invalid_credentials", "Invalid email or password");
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            await HandleFailedLoginAttempt(user);
            var remaining = _options.Lockout.MaxFailedAttempts - user.FailedLoginAttempts;
            _logger.LogDebug(
                "Authentication failed: invalid password for user {UserId} (created {CreatedAt}), {Remaining} attempts remaining. Password hash length: {HashLength}",
                user.Id,
                user.CreatedAt,
                remaining,
                user.PasswordHash?.Length ?? 0
            );
            return LocalAuthResult.Failed(
                "invalid_credentials",
                "Invalid email or password",
                remainingAttempts: Math.Max(0, remaining)
            );
        }

        // Success - reset failed attempts and update last login
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = ipAddress;
        user.UpdatedAt = DateTime.UtcNow;

        if (user.Subject != null)
        {
            user.Subject.LastLoginAt = DateTime.UtcNow;
            user.Subject.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} authenticated successfully", user.Id);

        return LocalAuthResult.Succeeded(
            MapToModel(user),
            user.SubjectId ?? throw new InvalidOperationException("User has no associated subject"),
            user.RequirePasswordChange
        );
    }

    /// <inheritdoc />
    public async Task<bool> VerifyEmailAsync(string token)
    {
        var tokenHash = HashToken(token);
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u =>
            u.EmailVerificationTokenHash == tokenHash
            && u.EmailVerificationTokenExpiresAt > DateTime.UtcNow
        );

        if (user == null)
        {
            _logger.LogDebug("Email verification failed: invalid or expired token");
            return false;
        }

        user.EmailVerified = true;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationTokenExpiresAt = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Email verified for user {UserId}", user.Id);

        // Send welcome email
        await _emailService.SendWelcomeAsync(user.Email, user.DisplayName);

        return true;
    }

    /// <inheritdoc />
    public async Task<PasswordResetRequestResult> RequestPasswordResetAsync(
        string email,
        string? ipAddress = null,
        string? userAgent = null
    )
    {
        var normalizedEmail = NormalizeEmail(email);
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u =>
            u.NormalizedEmail == normalizedEmail
        );

        // Always return success to prevent email enumeration
        var result = new PasswordResetRequestResult
        {
            Processed = true,
            Message =
                "If an account exists with this email, you will receive password reset instructions.",
        };

        if (user == null)
        {
            _logger.LogDebug("Password reset requested for non-existent email {Email}", email);
            return result;
        }

        // Generate reset token
        var resetToken = GenerateSecureToken();
        user.PasswordResetTokenHash = HashToken(resetToken);
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddHours(
            _options.Tokens.PasswordResetTokenHours
        );
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        if (_emailService.IsEnabled)
        {
            // Send reset email
            var baseUrl = _emailOptions.BaseUrl?.TrimEnd('/') ?? "";
            var resetUrl = $"{baseUrl}/auth/reset-password?token={resetToken}";
            await _emailService.SendPasswordResetAsync(user.Email, user.DisplayName, resetUrl);
            result.EmailSent = true;
        }
        else
        {
            // Create a password reset request for admin to handle
            var resetRequest = new PasswordResetRequestEntity
            {
                Id = Guid.CreateVersion7(),
                LocalUserId = user.Id,
                RequestedFromIp = ipAddress,
                UserAgent = userAgent,
                AdminNotified = false,
                Handled = false,
                CreatedAt = DateTime.UtcNow,
            };

            _dbContext.PasswordResetRequests.Add(resetRequest);
            await _dbContext.SaveChangesAsync();

            // Broadcast to admin subscribers via SignalR
            await _signalRBroadcastService.BroadcastPasswordResetRequestAsync();

            // Notify admin
            await _emailService.SendAdminPasswordResetRequestNotificationAsync(
                user.Email,
                user.DisplayName,
                resetRequest.Id
            );

            result.AdminNotificationRequired = true;
            result.Message =
                "Password reset request has been submitted. An administrator will contact you with instructions.";
        }

        _logger.LogInformation("Password reset requested for user {UserId}", user.Id);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> ResetPasswordAsync(string token, string newPassword)
    {
        // Validate password
        var passwordValidation = ValidatePassword(newPassword);
        if (!passwordValidation.IsValid)
        {
            _logger.LogDebug("Password reset failed: password validation failed");
            return false;
        }

        var tokenHash = HashToken(token);
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u =>
            u.PasswordResetTokenHash == tokenHash && u.PasswordResetTokenExpiresAt > DateTime.UtcNow
        );

        if (user == null)
        {
            _logger.LogDebug("Password reset failed: invalid or expired token");
            return false;
        }

        user.PasswordHash = HashPassword(newPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresAt = null;
        user.RequirePasswordChange = false;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Password reset successful for user {UserId}", user.Id);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword
    )
    {
        var user = await _dbContext.LocalUsers.FindAsync(userId);
        if (user == null)
        {
            return false;
        }

        // Verify current password
        if (
            string.IsNullOrEmpty(user.PasswordHash)
            || !VerifyPassword(currentPassword, user.PasswordHash)
        )
        {
            return false;
        }

        // Validate new password
        var passwordValidation = ValidatePassword(newPassword);
        if (!passwordValidation.IsValid)
        {
            return false;
        }

        user.PasswordHash = HashPassword(newPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        user.RequirePasswordChange = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Password changed for user {UserId}", userId);

        return true;
    }

    /// <inheritdoc />
    public async Task<LocalUser?> GetUserByIdAsync(Guid userId)
    {
        var user = await _dbContext.LocalUsers.FindAsync(userId);
        return user == null ? null : MapToModel(user);
    }

    /// <inheritdoc />
    public async Task<LocalUser?> GetUserByEmailAsync(string email)
    {
        var normalizedEmail = NormalizeEmail(email);
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u =>
            u.NormalizedEmail == normalizedEmail
        );
        return user == null ? null : MapToModel(user);
    }

    /// <inheritdoc />
    public async Task<List<LocalUser>> GetUsersAsync(LocalUserFilter? filter = null)
    {
        var query = _dbContext.LocalUsers.AsQueryable();

        if (filter != null)
        {
            if (filter.PendingApproval.HasValue)
                query = query.Where(u => u.PendingApproval == filter.PendingApproval.Value);

            if (filter.IsActive.HasValue)
                query = query.Where(u => u.IsActive == filter.IsActive.Value);

            if (filter.EmailVerified.HasValue)
                query = query.Where(u => u.EmailVerified == filter.EmailVerified.Value);

            if (!string.IsNullOrEmpty(filter.SearchTerm))
            {
                var term = filter.SearchTerm.ToLowerInvariant();
                query = query.Where(u =>
                    u.NormalizedEmail.Contains(term)
                    || (u.DisplayName != null && u.DisplayName.ToLower().Contains(term))
                );
            }
        }

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> ApproveUserAsync(Guid userId, Guid adminId)
    {
        var user = await _dbContext
            .LocalUsers.Include(u => u.Subject)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || !user.PendingApproval)
        {
            return false;
        }

        user.PendingApproval = false;
        user.IsActive = true;
        user.AdminNotes = $"Approved by admin on {DateTime.UtcNow:u}";
        user.UpdatedAt = DateTime.UtcNow;

        if (user.Subject != null)
        {
            user.Subject.IsActive = true;
            user.Subject.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} approved by admin {AdminId}", userId, adminId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RejectUserAsync(Guid userId, Guid adminId, string? reason = null)
    {
        var user = await _dbContext
            .LocalUsers.Include(u => u.Subject)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        user.IsActive = false;
        user.AdminNotes =
            $"Rejected by admin on {DateTime.UtcNow:u}. Reason: {reason ?? "Not specified"}";
        user.UpdatedAt = DateTime.UtcNow;

        if (user.Subject != null)
        {
            user.Subject.IsActive = false;
            user.Subject.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "User {UserId} rejected by admin {AdminId}. Reason: {Reason}",
            userId,
            adminId,
            reason
        );

        return true;
    }

    /// <inheritdoc />
    public async Task<List<PasswordResetRequest>> GetPendingPasswordResetRequestsAsync()
    {
        var requests = await _dbContext
            .PasswordResetRequests.Include(r => r.LocalUser)
            .Where(r => !r.Handled)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return requests
            .Select(r => new PasswordResetRequest
            {
                Id = r.Id,
                Email = r.LocalUser?.Email ?? "unknown",
                DisplayName = r.LocalUser?.DisplayName,
                RequestedFromIp = r.RequestedFromIp,
                UserAgent = r.UserAgent,
                CreatedAt = r.CreatedAt,
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> HandlePasswordResetRequestAsync(Guid requestId, Guid adminId)
    {
        var request = await _dbContext
            .PasswordResetRequests.Include(r => r.LocalUser)
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.Handled);

        if (request?.LocalUser == null)
        {
            return null;
        }

        // Generate reset token
        var resetToken = GenerateSecureToken();
        request.LocalUser.PasswordResetTokenHash = HashToken(resetToken);
        request.LocalUser.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddHours(
            _options.Tokens.PasswordResetTokenHours
        );
        request.LocalUser.UpdatedAt = DateTime.UtcNow;

        request.Handled = true;
        request.HandledById = adminId;
        request.HandledAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // Broadcast update to refresh admin UI
        await _signalRBroadcastService.BroadcastPasswordResetRequestAsync();

        var baseUrl = _emailOptions.BaseUrl?.TrimEnd('/') ?? "";
        var resetUrl = $"{baseUrl}/auth/reset-password?token={resetToken}";

        _logger.LogInformation(
            "Password reset request {RequestId} handled by admin {AdminId}",
            requestId,
            adminId
        );

        return resetUrl;
    }

    /// <inheritdoc />
    public async Task<bool> SetTemporaryPasswordAsync(
        Guid userId,
        string temporaryPassword,
        Guid adminId
    )
    {
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            _logger.LogWarning("SetTemporaryPassword failed: User {UserId} not found", userId);
            return false;
        }

        if (!string.IsNullOrEmpty(temporaryPassword))
        {
            var validation = ValidatePassword(temporaryPassword);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "SetTemporaryPassword failed: Password validation failed for user {UserId}",
                    userId
                );
                return false;
            }
        }

        user.PasswordHash = HashPassword(temporaryPassword);
        user.RequirePasswordChange = true;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresAt = null;

        var pendingRequests = await _dbContext
            .PasswordResetRequests.Where(r => r.LocalUserId == userId && !r.Handled)
            .ToListAsync();

        foreach (var request in pendingRequests)
        {
            request.Handled = true;
            request.HandledById = adminId;
            request.HandledAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        // Broadcast update if we cleared any pending requests
        if (pendingRequests.Count > 0)
        {
            await _signalRBroadcastService.BroadcastPasswordResetRequestAsync();
        }

        _logger.LogInformation(
            "Admin {AdminId} set temporary password for user {UserId} ({Email})",
            adminId,
            userId,
            user.Email
        );

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ClearRequirePasswordChangeAsync(Guid userId)
    {
        var user = await _dbContext.LocalUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return false;
        }

        user.RequirePasswordChange = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return true;
    }

    /// <inheritdoc />
    public AllowlistCheckResult CheckAllowlist(string email)
    {
        if (!_options.Allowlist.Enabled)
        {
            return new AllowlistCheckResult { IsAllowed = true };
        }

        var normalizedEmail = NormalizeEmail(email);
        var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant();

        // Check exact email match
        if (_options.Allowlist.AllowedEmails.Any(e => NormalizeEmail(e) == normalizedEmail))
        {
            return new AllowlistCheckResult { IsAllowed = true };
        }

        // Check domain match
        if (
            domain != null
            && _options.Allowlist.AllowedDomains.Any(d => d.ToLowerInvariant() == domain)
        )
        {
            return new AllowlistCheckResult { IsAllowed = true };
        }

        // Check if others can register with approval
        if (_options.Allowlist.AllowOthersWithApproval)
        {
            return new AllowlistCheckResult { IsAllowed = true, RequiresApproval = true };
        }

        return new AllowlistCheckResult
        {
            IsAllowed = false,
            Reason = "Email is not on the allowed list. Contact an administrator for access.",
        };
    }

    /// <inheritdoc />
    public PasswordValidationResult ValidatePassword(string password)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required");
            return PasswordValidationResult.Invalid(errors.ToArray());
        }

        if (password.Length < _options.Password.MinLength)
        {
            errors.Add($"Password must be at least {_options.Password.MinLength} characters");
        }

        if (password.Length > _options.Password.MaxLength)
        {
            errors.Add($"Password must be at most {_options.Password.MaxLength} characters");
        }

        if (_options.Password.RequireUppercase && !password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter");
        }

        if (_options.Password.RequireLowercase && !password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter");
        }

        if (_options.Password.RequireDigit && !password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one digit");
        }

        if (
            _options.Password.RequireSpecialCharacter
            && !password.Any(c => !char.IsLetterOrDigit(c))
        )
        {
            errors.Add("Password must contain at least one special character");
        }

        return errors.Count == 0
            ? PasswordValidationResult.Valid()
            : PasswordValidationResult.Invalid(errors.ToArray());
    }

    #region Private Helpers

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleFailedLoginAttempt(LocalUserEntity user)
    {
        user.FailedLoginAttempts++;
        user.UpdatedAt = DateTime.UtcNow;

        if (
            _options.Lockout.Enabled
            && user.FailedLoginAttempts >= _options.Lockout.MaxFailedAttempts
        )
        {
            var lockoutDuration = _options.Lockout.LockoutDurationMinutes;

            if (_options.Lockout.ExponentialBackoff)
            {
                // Calculate exponential backoff based on number of lockouts
                var lockoutMultiplier =
                    user.FailedLoginAttempts / _options.Lockout.MaxFailedAttempts;
                lockoutDuration = Math.Min(
                    lockoutDuration * (int)Math.Pow(2, lockoutMultiplier - 1),
                    _options.Lockout.MaxLockoutDurationMinutes
                );
            }

            user.LockedUntil = DateTime.UtcNow.AddMinutes(lockoutDuration);
            _logger.LogWarning(
                "User {UserId} locked out for {Minutes} minutes after {Attempts} failed attempts",
                user.Id,
                lockoutDuration,
                user.FailedLoginAttempts
            );
        }

        await _dbContext.SaveChangesAsync();
    }

    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashPassword(string password)
    {
        // Generate salt
        var salt = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);

        // Hash with Argon2id
        using var argon2 = new Argon2id(GetPasswordBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 4,
            MemorySize = 65536, // 64 MB
            Iterations = 3,
        };

        var hash = argon2.GetBytes(32);

        // Combine salt and hash for storage
        var combined = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, combined, salt.Length, hash.Length);

        return Convert.ToBase64String(combined);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            var combined = Convert.FromBase64String(storedHash);
            if (combined.Length != 48) // 16 bytes salt + 32 bytes hash
                return false;

            var salt = new byte[16];
            var hash = new byte[32];
            Buffer.BlockCopy(combined, 0, salt, 0, 16);
            Buffer.BlockCopy(combined, 16, hash, 0, 32);

            using var argon2 = new Argon2id(GetPasswordBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = 4,
                MemorySize = 65536,
                Iterations = 3,
            };

            var computedHash = argon2.GetBytes(32);
            return CryptographicOperations.FixedTimeEquals(hash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] GetPasswordBytes(string password)
    {
        return string.IsNullOrEmpty(password)
            ? new byte[] { 0 }
            : Encoding.UTF8.GetBytes(password);
    }

    private static LocalUser MapToModel(LocalUserEntity entity)
    {
        return new LocalUser
        {
            Id = entity.Id,
            Email = entity.Email,
            DisplayName = entity.DisplayName,
            EmailVerified = entity.EmailVerified,
            IsActive = entity.IsActive,
            PendingApproval = entity.PendingApproval,
            RequirePasswordChange = entity.RequirePasswordChange,
            SubjectId = entity.SubjectId,
            LastLoginAt = entity.LastLoginAt,
            CreatedAt = entity.CreatedAt,
        };
    }

    #endregion
}
