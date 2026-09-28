using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// OAuth refresh tokens. One grant can have multiple active refresh tokens
/// (same app on two devices). Refresh tokens are rotated on each use.
/// </summary>
[Table("oauth_refresh_tokens")]
public class OAuthRefreshTokenEntity : ITenantScoped
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant that owns this token (denormalized from the parent grant for RLS)
    /// </summary>
    [Required]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Foreign key to the grant this token belongs to
    /// </summary>
    [Required]
    [Column("grant_id")]
    public Guid GrantId { get; set; }

    /// <summary>
    /// SHA-256 hash of the opaque refresh token
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("token_hash")]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// When this token was issued.
    /// NocturneDbContext stamps this on insert, unconditionally: a value assigned before the insert
    /// does not survive it, so an imported token's original issue time can only be applied by a
    /// second save against the inserted row.
    /// </summary>
    [Column("issued_at")]
    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// When this token expires (90 days from issuance)
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When this token was revoked (null if active)
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// If this token was rotated, the ID of the replacement token.
    /// Used for rotation chain tracking and detecting token reuse attacks.
    /// </summary>
    [Column("replaced_by_id")]
    public Guid? ReplacedById { get; set; }

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

    // Navigation properties

    /// <summary>
    /// The grant this token belongs to
    /// </summary>
    public OAuthGrantEntity? Grant { get; set; }

    /// <summary>
    /// The token that replaced this one (if rotated)
    /// </summary>
    public OAuthRefreshTokenEntity? ReplacedBy { get; set; }
}
