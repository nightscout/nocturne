using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Represents an invitation for a subject to join a tenant with specific roles and permissions.
/// </summary>
/// <remarks>
/// Tenant-scoped: the row holds a bearer credential that grants membership of exactly one tenant,
/// so it carries the global query filter and the <c>tenant_isolation</c> RLS policy rather than
/// relying on every call site remembering its own tenant predicate.
/// </remarks>
[Table("member_invites")]
public class MemberInviteEntity : ITenantScoped
{
    /// <summary>
    /// Unique identifier for the member invite
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The unique identifier of the tenant this invite grants membership of.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the subject who created the invite
    /// </summary>
    [Column("created_by_subject_id")]
    public Guid CreatedBySubjectId { get; set; }

    /// <summary>
    /// SHA-256 hash of the invitation token
    /// </summary>
    [Required]
    [Column("token_hash")]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// List of tenant role identifiers to be assigned when the invite is used
    /// </summary>
    [Required]
    [Column("role_ids", TypeName = "jsonb")]
    public List<Guid> RoleIds { get; set; } = [];

    /// <summary>
    /// Optional list of direct permissions to be assigned when the invite is used
    /// </summary>
    [Column("direct_permissions", TypeName = "jsonb")]
    public List<string>? DirectPermissions { get; set; }

    /// <summary>
    /// Optional human-readable label for the invite
    /// </summary>
    [Column("label")]
    [MaxLength(255)]
    public string? Label { get; set; }

    /// <summary>
    /// Whether the invite validity should be limited to 24 hours
    /// </summary>
    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; } = false;

    /// <summary>
    /// When the invite expires
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Maximum number of times the invite can be used
    /// </summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    /// <summary>
    /// Number of times the invite has been used
    /// </summary>
    [Column("use_count")]
    public int UseCount { get; set; } = 0;

    /// <summary>
    /// When the invite was revoked, if applicable
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When the invite was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the invite is currently expired
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether the invite has been revoked
    /// </summary>
    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>
    /// Whether the invite has reached its maximum usage limit
    /// </summary>
    [NotMapped]
    public bool IsExhausted => MaxUses.HasValue && UseCount >= MaxUses.Value;

    /// <summary>
    /// Whether the invite is currently valid for use
    /// </summary>
    [NotMapped]
    public bool IsValid => !IsExpired && !IsRevoked && !IsExhausted;

    /// <summary>
    /// Navigation property to the tenant the invite is for
    /// </summary>
    public TenantEntity? Tenant { get; set; }

    /// <summary>
    /// Navigation property to the subject who created the invite
    /// </summary>
    public SubjectEntity? CreatedBy { get; set; }

    /// <summary>
    /// Collection of members created using this invite
    /// </summary>
    public ICollection<TenantMemberEntity> CreatedMembers { get; set; } = new List<TenantMemberEntity>();
}
