using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Join entity linking subjects to tenants (many-to-many).
/// </summary>
[Table("tenant_members")]
public class TenantMemberEntity : ISystemTimestamped
{
    /// <summary>
    /// Unique identifier for the tenant member link
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the subject (user/service)
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// When the membership was created
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the membership record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional list of direct permissions assigned to this member within the tenant
    /// </summary>
    [Column("direct_permissions", TypeName = "jsonb")]
    public List<string>? DirectPermissions { get; set; }

    /// <summary>
    /// Optional human-readable label for the membership
    /// </summary>
    [Column("label")]
    [MaxLength(255)]
    public string? Label { get; set; }

    /// <summary>
    /// Per-tenant display username for this member
    /// </summary>
    [Column("username")]
    [MaxLength(50)]
    public string? Username { get; set; }

    /// <summary>
    /// Whether this member reads only the last 24 hours of every time-series category. An owner or a
    /// holder of <c>tenant.settings</c> is never clamped, whatever this says.
    /// </summary>
    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; } = false;

    /// <summary>
    /// Identifier of the invite used to create this membership, if applicable
    /// </summary>
    [Column("created_from_invite_id")]
    public Guid? CreatedFromInviteId { get; set; }

    /// <summary>
    /// When the member last accessed the tenant
    /// </summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// IP address of the member's last access
    /// </summary>
    [Column("last_used_ip")]
    [MaxLength(45)]
    public string? LastUsedIp { get; set; }

    /// <summary>
    /// User agent of the member's last access
    /// </summary>
    [Column("last_used_user_agent")]
    public string? LastUsedUserAgent { get; set; }

    /// <summary>
    /// Navigation property to the tenant
    /// </summary>
    public TenantEntity? Tenant { get; set; }

    /// <summary>
    /// Navigation property to the subject
    /// </summary>
    public SubjectEntity? Subject { get; set; }

    /// <summary>
    /// Navigation property to the invite used to create this membership
    /// </summary>
    public MemberInviteEntity? CreatedFromInvite { get; set; }

    /// <summary>
    /// Collection of roles assigned to this member within the tenant
    /// </summary>
    public List<TenantMemberRoleEntity> MemberRoles { get; set; } = [];

    /// <summary>
    /// The membership's effective permissions: every role's permissions unioned with the direct
    /// permissions. Requires <see cref="MemberRoles"/> to be loaded with their roles.
    /// </summary>
    /// <param name="excludingRoleId">A role to leave out, to ask what would remain without it.</param>
    public IEnumerable<string> EffectivePermissions(Guid? excludingRoleId = null) =>
        MemberPermissions.Effective(
            MemberRoles
                .Where(mr => mr.TenantRoleId != excludingRoleId)
                .Select(mr => mr.TenantRole.Permissions),
            DirectPermissions);

    /// <summary>
    /// Whether the effective permissions, normalised as member permissions, satisfy
    /// <paramref name="required"/> through <see cref="Scope.Satisfies(IEnumerable{string}, string)"/>.
    /// Only role and direct permissions count: the member-personal device scopes that request
    /// resolution adds for any member with a permission are not included. Requires
    /// <see cref="MemberRoles"/> to be loaded with their roles.
    /// </summary>
    public bool Holds(string required) =>
        Scope.Satisfies(Scope.NormalizeMemberPermissions(EffectivePermissions()), required);
}
