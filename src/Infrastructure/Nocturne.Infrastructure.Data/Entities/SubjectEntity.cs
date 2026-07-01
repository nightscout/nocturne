using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Subjects (users/devices) - enhanced from legacy Nightscout
/// Represents both human users and automated devices that can authenticate
/// </summary>
[Table("subjects")]
public class SubjectEntity : IEntityTimestamped
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name of the subject
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Login identifier for non-discoverable WebAuthn flows
    /// </summary>
    [MaxLength(50)]
    [Column("username")]
    public string? Username { get; set; }

    /// <summary>
    /// SHA256 hash of legacy access token for secure lookup
    /// </summary>
    [MaxLength(64)]
    [Column("access_token_hash")]
    public string? AccessTokenHash { get; set; }

    /// <summary>
    /// Display prefix for access token (e.g., "rhys-a1b2..." for UI display)
    /// </summary>
    [MaxLength(50)]
    [Column("access_token_prefix")]
    public string? AccessTokenPrefix { get; set; }

    /// <summary>
    /// Email address (from OIDC claims or manually set)
    /// </summary>
    [MaxLength(255)]
    [Column("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Notes or description about this subject
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether this subject is currently active and can authenticate
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-generated subject (cannot be deleted)
    /// </summary>
    [Column("is_system_subject")]
    public bool IsSystemSubject { get; set; }

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

    /// <summary>
    /// When this subject last logged in
    /// </summary>
    [Column("last_login_at")]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId for migration tracking
    /// </summary>
    [MaxLength(24)]
    [Column("original_id")]
    public string? OriginalId { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    [MaxLength(10)]
    [Column("preferred_language")]
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Approval status for access requests (e.g., "Approved", "Pending", "Denied")
    /// Defaults to "Approved" for existing subjects
    /// </summary>
    [Required]
    [MaxLength(20)]
    [Column("approval_status")]
    public string ApprovalStatus { get; set; } = "Approved";

    /// <summary>
    /// Optional message submitted with an access request
    /// </summary>
    [MaxLength(500)]
    [Column("access_request_message")]
    public string? AccessRequestMessage { get; set; }

    /// <summary>
    /// Whether this subject has platform-level admin access (not tenant-scoped).
    /// Platform admins can manage OIDC providers and other platform-wide configuration.
    /// </summary>
    [Column("is_platform_admin")]
    public bool IsPlatformAdmin { get; set; } = false;

    /// <summary>
    /// URL to the subject's avatar image, if set
    /// </summary>
    [MaxLength(2048)]
    [Column("avatar_url")]
    public string? AvatarUrl { get; set; }

    // Navigation properties

    /// <summary>
    /// Subject-role mappings for this subject
    /// </summary>
    public ICollection<SubjectRoleEntity> SubjectRoles { get; set; } = new List<SubjectRoleEntity>();

    /// <summary>
    /// Refresh tokens issued to this subject
    /// </summary>
    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = new List<RefreshTokenEntity>();

    /// <summary>
    /// Passkey credentials registered by this subject
    /// </summary>
    public ICollection<PasskeyCredentialEntity> PasskeyCredentials { get; set; } = new List<PasskeyCredentialEntity>();

    /// <summary>
    /// TOTP credentials registered by this subject
    /// </summary>
    public ICollection<TotpCredentialEntity> TotpCredentials { get; set; } = new List<TotpCredentialEntity>();

    /// <summary>
    /// OIDC provider identities linked to this subject
    /// </summary>
    public ICollection<SubjectOidcIdentityEntity> OidcIdentities { get; set; } = new List<SubjectOidcIdentityEntity>();
}
