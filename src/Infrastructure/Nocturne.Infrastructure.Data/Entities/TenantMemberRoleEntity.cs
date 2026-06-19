using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Join entity linking tenant members to roles (many-to-many).
/// </summary>
[Table("tenant_member_roles")]
public class TenantMemberRoleEntity : ISystemCreated
{
    /// <summary>
    /// Unique identifier for the member role link
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant member
    /// </summary>
    [Required]
    [Column("tenant_member_id")]
    public Guid TenantMemberId { get; set; }

    /// <summary>
    /// Navigation property to the tenant member
    /// </summary>
    [ForeignKey(nameof(TenantMemberId))]
    public TenantMemberEntity TenantMember { get; set; } = null!;

    /// <summary>
    /// Identifier of the role assigned to the member
    /// </summary>
    [Required]
    [Column("tenant_role_id")]
    public Guid TenantRoleId { get; set; }

    /// <summary>
    /// Navigation property to the role assigned to the member
    /// </summary>
    [ForeignKey(nameof(TenantRoleId))]
    public TenantRoleEntity TenantRole { get; set; } = null!;

    /// <summary>
    /// When the role assignment was created
    /// </summary>
    [Required]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
