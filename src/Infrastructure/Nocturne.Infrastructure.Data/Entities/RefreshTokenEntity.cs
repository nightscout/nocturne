using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Refresh tokens for session continuity
/// Stored in database for revocation support - access tokens are short-lived JWTs validated statelessly
/// </summary>
[Table("refresh_tokens")]
public class RefreshTokenEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// SHA256 hash of the refresh token for secure lookup
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("token_hash")]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to the subject (user/device) this token belongs to
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Navigation property to the subject
    /// </summary>
    public SubjectEntity? Subject { get; set; }

    /// <summary>
    /// OIDC session ID for RP-initiated logout (revoke all tokens in session)
    /// </summary>
    [MaxLength(255)]
    [Column("oidc_session_id")]
    public string? OidcSessionId { get; set; }

    /// <summary>
    /// When the token was issued
    /// </summary>
    [Column("issued_at")]
    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// When the token expires (typically 7-30 days)
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the token was last used to obtain a new access token
    /// </summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// When the token was revoked (null if active)
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Reason for revocation
    /// </summary>
    [MaxLength(255)]
    [Column("revoked_reason")]
    public string? RevokedReason { get; set; }

    /// <summary>
    /// If this token was rotated, the ID of the replacement token
    /// Used for detecting token reuse attacks
    /// </summary>
    [Column("replaced_by_token_id")]
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// Device/client description for session management UI
    /// </summary>
    [MaxLength(500)]
    [Column("device_description")]
    public string? DeviceDescription { get; set; }

    /// <summary>
    /// IP address when token was issued
    /// </summary>
    [MaxLength(45)]
    [Column("ip_address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent when token was issued
    /// </summary>
    [Column("user_agent")]
    public string? UserAgent { get; set; }

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
    /// Whether this token has been revoked
    /// </summary>
    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>
    /// Whether this token has expired
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether this token is currently valid (not expired and not revoked)
    /// </summary>
    [NotMapped]
    public bool IsValid => !IsRevoked && !IsExpired;
}
