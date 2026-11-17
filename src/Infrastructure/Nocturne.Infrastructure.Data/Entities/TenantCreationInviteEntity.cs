using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A platform admin's link that lets whoever holds it create an account and a tenant they own.
/// Not tenant-scoped: it belongs to the instance, and no tenant exists yet when it is redeemed.
/// </summary>
[Table("tenant_creation_invites")]
public class TenantCreationInviteEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("created_by_subject_id")]
    public Guid CreatedBySubjectId { get; set; }

    public SubjectEntity? CreatedBy { get; set; }

    /// <summary>SHA-256 hex of the bearer token in the link; the token itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    [Column("token_hash")]
    public string TokenHash { get; set; } = string.Empty;

    [MaxLength(255)]
    [Column("label")]
    public string? Label { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>How many tenants the link may create; null for no cap.</summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    [Column("use_count")]
    public int UseCount { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    public List<TenantCreationInviteUseEntity> Uses { get; set; } = [];
}

/// <summary>One tenant a <see cref="TenantCreationInviteEntity"/> created.</summary>
[Table("tenant_creation_invite_uses")]
public class TenantCreationInviteUseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("invite_id")]
    public Guid InviteId { get; set; }

    /// <summary>The tenant created, or null once it has been deleted.</summary>
    [Column("tenant_id")]
    public Guid? TenantId { get; set; }

    public TenantEntity? Tenant { get; set; }

    [Column("redeemed_at")]
    public DateTime RedeemedAt { get; set; }
}

/// <summary>
/// The instance's platform policy, one row with id 1. Not tenant-scoped.
/// </summary>
[Table("platform_policy")]
public class PlatformPolicyEntity : ISystemTimestamped
{
    public const int SingletonId = 1;

    [Key]
    [Column("id")]
    public int Id { get; set; } = SingletonId;

    /// <inheritdoc cref="Nocturne.Core.Models.Identity.PlatformPolicy.AllowOwnerSlugChange"/>
    [Column("allow_owner_slug_change")]
    public bool AllowOwnerSlugChange { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}
