using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Identity;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One reason a member holds its access. The member row stores the <see cref="MembershipSum"/> of
/// its reasons.
/// </summary>
/// <remarks>
/// Neither tenant-scoped nor under row level security, like <see cref="TenantMemberRoleEntity"/>: it is
/// reached only through a member row the caller can already read, and the subject-pinned and platform
/// contexts that remove memberships across tenants must see it.
/// </remarks>
[Table("tenant_member_reasons")]
public class TenantMemberReasonEntity : ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("tenant_member_id")]
    public Guid TenantMemberId { get; set; }

    [ForeignKey(nameof(TenantMemberId))]
    public TenantMemberEntity TenantMember { get; set; } = null!;

    /// <summary>A <see cref="MembershipReasonKind"/>.</summary>
    [Required]
    [Column("kind")]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// What the reason comes from: for <see cref="MembershipReasonKind.Team"/> the team's access to the
    /// tenant (<see cref="TeamAccess"/>); null for <see cref="MembershipReasonKind.Direct"/>.
    /// </summary>
    [Column("source_id")]
    public Guid? SourceId { get; set; }

    public TeamTenantAccessEntity? TeamAccess { get; set; }

    [Column("direct_permissions", TypeName = "jsonb")]
    public List<string>? DirectPermissions { get; set; }

    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

    /// <summary>When the reason ends, in UTC; null when it does not.</summary>
    [Column("access_ends_at")]
    public DateTime? AccessEndsAt { get; set; }

    [Column("created_from_invite_id")]
    public Guid? CreatedFromInviteId { get; set; }

    public MemberInviteEntity? CreatedFromInvite { get; set; }

    public List<TenantMemberReasonRoleEntity> ReasonRoles { get; set; } = [];

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}
