using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A role a <see cref="TenantMemberReasonEntity"/> gives. The link is stored rather than the role's
/// permissions, which can change later.
/// </summary>
[Table("tenant_member_reason_roles")]
public class TenantMemberReasonRoleEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("tenant_member_reason_id")]
    public Guid TenantMemberReasonId { get; set; }

    [ForeignKey(nameof(TenantMemberReasonId))]
    public TenantMemberReasonEntity TenantMemberReason { get; set; } = null!;

    [Required]
    [Column("tenant_role_id")]
    public Guid TenantRoleId { get; set; }

    [ForeignKey(nameof(TenantRoleId))]
    public TenantRoleEntity TenantRole { get; set; } = null!;

    [Required]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
