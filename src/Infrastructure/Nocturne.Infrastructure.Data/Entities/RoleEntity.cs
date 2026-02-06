using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Roles - enhanced from legacy Nightscout
/// Defines sets of permissions that can be assigned to subjects
/// </summary>
[Table("roles")]
public class RoleEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Unique name of the role (e.g., "admin", "readable", "careportal")
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the role
    /// </summary>
    [MaxLength(500)]
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Shiro-style permissions for this role (stored as JSON array)
    /// Examples: "*", "api:entries:read", "api:treatments:create"
    /// </summary>
    [Column("permissions", TypeName = "jsonb")]
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Notes or additional information about this role
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether this is a system-generated default role that cannot be deleted
    /// </summary>
    [Column("is_system_role")]
    public bool IsSystemRole { get; set; } = false;

    /// <summary>
    /// Original MongoDB ObjectId for migration tracking
    /// </summary>
    [MaxLength(24)]
    [Column("original_id")]
    public string? OriginalId { get; set; }

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

    // Navigation property

    /// <summary>
    /// Subject-role mappings for this role
    /// </summary>
    public ICollection<SubjectRoleEntity> SubjectRoles { get; set; } = new List<SubjectRoleEntity>();
}
