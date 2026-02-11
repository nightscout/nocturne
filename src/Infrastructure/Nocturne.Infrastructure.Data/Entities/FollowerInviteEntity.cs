using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Invite links for follower grants. Allows data owners to share their data with
/// friends/family who don't have accounts yet. The invitee clicks the link, signs in
/// (or creates an account), and the follower grant is automatically created.
/// </summary>
[Table("follower_invites")]
public class FollowerInviteEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the subject (data owner) who created this invite
    /// </summary>
    [Required]
    [Column("owner_subject_id")]
    public Guid OwnerSubjectId { get; set; }

    /// <summary>
    /// SHA-256 hash of the invite token. The actual token is only shown once at creation.
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("token_hash")]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Scopes to grant when the invite is accepted (stored as JSON array)
    /// </summary>
    [Required]
    [Column("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Optional label for the grant (e.g., "Mom", "Endocrinologist")
    /// </summary>
    [MaxLength(100)]
    [Column("label")]
    public string? Label { get; set; }

    /// <summary>
    /// When this invite expires
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Maximum number of times this invite can be used (null = unlimited)
    /// </summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    /// <summary>
    /// Number of times this invite has been used
    /// </summary>
    [Column("use_count")]
    public int UseCount { get; set; } = 0;

    /// <summary>
    /// When this invite was revoked (null = not revoked)
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When this invite was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When true, grants created from this invite will limit data access to
    /// the last 24 hours (rolling window from each request time).
    /// </summary>
    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

    /// <summary>
    /// Whether this invite has expired
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether this invite has been revoked
    /// </summary>
    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>
    /// Whether this invite has reached its max uses
    /// </summary>
    [NotMapped]
    public bool IsExhausted => MaxUses.HasValue && UseCount >= MaxUses.Value;

    /// <summary>
    /// Whether this invite is currently valid (not expired, not revoked, not exhausted)
    /// </summary>
    [NotMapped]
    public bool IsValid => !IsExpired && !IsRevoked && !IsExhausted;

    // Navigation properties

    /// <summary>
    /// The data owner who created this invite
    /// </summary>
    public SubjectEntity? Owner { get; set; }

    /// <summary>
    /// Grants that were created from this invite
    /// </summary>
    public ICollection<OAuthGrantEntity> CreatedGrants { get; set; } = new List<OAuthGrantEntity>();
}
