using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Subjects (users/devices) - enhanced from legacy Nightscout
/// Represents both human users and automated devices that can authenticate
/// </summary>
[Table("subjects")]
public class SubjectEntity : IHasCreatedAt, IHasUpdatedAt
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
    /// Link to OIDC identity - external 'sub' claim
    /// </summary>
    [MaxLength(255)]
    [Column("oidc_subject_id")]
    public string? OidcSubjectId { get; set; }

    /// <summary>
    /// OIDC issuer URL for this subject's identity
    /// </summary>
    [MaxLength(500)]
    [Column("oidc_issuer")]
    public string? OidcIssuer { get; set; }

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

    // Navigation properties

    /// <summary>
    /// Subject-role mappings for this subject
    /// </summary>
    public ICollection<SubjectRoleEntity> SubjectRoles { get; set; } = new List<SubjectRoleEntity>();

    /// <summary>
    /// Refresh tokens issued to this subject
    /// </summary>
    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = new List<RefreshTokenEntity>();
}
