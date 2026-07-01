using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Represents a role within a tenant, defining a set of permissions for members.
/// </summary>
[Table("tenant_roles")]
public class TenantRoleEntity : ISystemTimestamped
{
    /// <summary>
    /// Unique identifier for the tenant role
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant this role belongs to.
    /// </summary>
    [Required]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Navigation property to the tenant this role belongs to
    /// </summary>
    [ForeignKey(nameof(TenantId))]
    public TenantEntity Tenant { get; set; } = null!;

    /// <summary>
    /// Human-readable name of the role
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable unique identifier for the role (e.g. "admin", "viewer")
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Column("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what permissions this role grants
    /// </summary>
    [MaxLength(500)]
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>
    /// List of permission strings assigned to this role (stored as JSON)
    /// </summary>
    [Required]
    [Column("permissions", TypeName = "jsonb")]
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// Whether this is a system-defined role that cannot be deleted or modified
    /// </summary>
    [Required]
    [Column("is_system")]
    public bool IsSystem { get; set; }

    /// <summary>
    /// When the role record was created
    /// </summary>
    [Required]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>
    /// When the role record was last updated
    /// </summary>
    [Required]
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }

    /// <summary>
    /// Collection of link entities between members and this role
    /// </summary>
    public List<TenantMemberRoleEntity> MemberRoles { get; set; } = [];
}
