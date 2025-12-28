using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Local identity provider users - for Nocturne's built-in authentication
/// This enables authentication without requiring external OIDC providers like Keycloak
/// </summary>
[Table("local_users")]
public class LocalUserEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Email address - used as login identifier
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Normalized email for case-insensitive lookup
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("normalized_email")]
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the user
    /// </summary>
    [MaxLength(255)]
    [Column("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Hashed password (Argon2id)
    /// </summary>
    [MaxLength(255)]
    [Column("password_hash")]
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Whether the email has been verified
    /// </summary>
    [Column("email_verified")]
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// Token for email verification (hashed)
    /// </summary>
    [MaxLength(64)]
    [Column("email_verification_token_hash")]
    public string? EmailVerificationTokenHash { get; set; }

    /// <summary>
    /// When the email verification token expires
    /// </summary>
    [Column("email_verification_token_expires_at")]
    public DateTime? EmailVerificationTokenExpiresAt { get; set; }

    /// <summary>
    /// Token for password reset (hashed)
    /// </summary>
    [MaxLength(64)]
    [Column("password_reset_token_hash")]
    public string? PasswordResetTokenHash { get; set; }

    /// <summary>
    /// When the password reset token expires
    /// </summary>
    [Column("password_reset_token_expires_at")]
    public DateTime? PasswordResetTokenExpiresAt { get; set; }

    /// <summary>
    /// Number of failed login attempts (for rate limiting)
    /// </summary>
    [Column("failed_login_attempts")]
    public int FailedLoginAttempts { get; set; } = 0;

    /// <summary>
    /// When the account is locked until (null if not locked)
    /// </summary>
    [Column("locked_until")]
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Whether the user account is active
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this user requires admin approval before they can login
    /// Used when allowlist is enabled but email is not pre-approved
    /// </summary>
    [Column("pending_approval")]
    public bool PendingApproval { get; set; } = false;

    /// <summary>
    /// Whether the user must change their password on next login
    /// Set by admin when providing a temporary password
    /// </summary>
    [Column("require_password_change")]
    public bool RequirePasswordChange { get; set; } = false;

    /// <summary>
    /// Notes from admin (e.g., approval notes)
    /// </summary>
    [Column("admin_notes")]
    public string? AdminNotes { get; set; }

    /// <summary>
    /// Foreign key to the associated Subject (for permissions)
    /// </summary>
    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// Navigation property to the Subject
    /// </summary>
    public SubjectEntity? Subject { get; set; }

    /// <summary>
    /// When this user last logged in
    /// </summary>
    [Column("last_login_at")]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// IP address of last login
    /// </summary>
    [MaxLength(45)]
    [Column("last_login_ip")]
    public string? LastLoginIp { get; set; }

    /// <summary>
    /// When the password was last changed
    /// </summary>
    [Column("password_changed_at")]
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>
    /// System tracking: when record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Pending password reset request - for admin notification when SMTP is not configured
/// </summary>
[Table("password_reset_requests")]
public class PasswordResetRequestEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the local user
    /// </summary>
    [Required]
    [Column("local_user_id")]
    public Guid LocalUserId { get; set; }

    /// <summary>
    /// Navigation property to the local user
    /// </summary>
    public LocalUserEntity? LocalUser { get; set; }

    /// <summary>
    /// IP address of the requestor
    /// </summary>
    [MaxLength(45)]
    [Column("requested_from_ip")]
    public string? RequestedFromIp { get; set; }

    /// <summary>
    /// User agent of the requestor
    /// </summary>
    [MaxLength(500)]
    [Column("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Whether the admin has been notified about this request
    /// </summary>
    [Column("admin_notified")]
    public bool AdminNotified { get; set; } = false;

    /// <summary>
    /// Whether this request has been handled by an admin
    /// </summary>
    [Column("handled")]
    public bool Handled { get; set; } = false;

    /// <summary>
    /// Admin ID who handled the request
    /// </summary>
    [Column("handled_by_id")]
    public Guid? HandledById { get; set; }

    /// <summary>
    /// Navigation property to the admin who handled
    /// </summary>
    public SubjectEntity? HandledBy { get; set; }

    /// <summary>
    /// When the request was handled
    /// </summary>
    [Column("handled_at")]
    public DateTime? HandledAt { get; set; }

    /// <summary>
    /// When this request was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
