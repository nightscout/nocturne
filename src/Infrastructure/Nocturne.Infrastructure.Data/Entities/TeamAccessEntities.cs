using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A link that lets whoever opens it, signed in, give a team access to a site they own. Not
/// tenant-scoped, like <see cref="TeamInviteEntity"/>, and kept apart from it so that neither token
/// can be redeemed as the other.
/// </summary>
[Table("team_access_links")]
public class TeamAccessLinkEntity : ISystemCreated, ITeamLink
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

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

    /// <summary>How many sites the link may be used for; null for no cap.</summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    [Column("use_count")]
    public int UseCount { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>A <c>RoleSeeds</c> slug: roles are per tenant, so the link cannot name a role id.</summary>
    [Required]
    [MaxLength(64)]
    [Column("suggested_role_slug")]
    public string SuggestedRoleSlug { get; set; } = string.Empty;

    [Column("suggested_limit_to_24_hours")]
    public bool SuggestedLimitTo24Hours { get; set; }

    [Column("suggested_access_ends_at")]
    public DateTime? SuggestedAccessEndsAt { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// A team's access to a site, approved by an owner of the site. Not <c>ITenantScoped</c>: the team
/// side reads it across sites. Its <c>tenant_pin</c> policy shows a context pinned to a tenant only
/// that tenant's rows.
/// </summary>
[Table("team_tenant_access")]
public class TeamTenantAccessEntity : ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    public TenantEntity? Tenant { get; set; }

    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

    [Column("access_ends_at")]
    public DateTime? AccessEndsAt { get; set; }

    [Column("approved_by_subject_id")]
    public Guid? ApprovedBySubjectId { get; set; }

    public SubjectEntity? ApprovedBy { get; set; }

    [Column("created_from_link_id")]
    public Guid? CreatedFromLinkId { get; set; }

    public TeamAccessLinkEntity? CreatedFromLink { get; set; }

    /// <summary>
    /// Whether the team's managers decide which of their staff reach the site: its staff are approved
    /// here as they join. When false each waits for the site to approve them.
    /// </summary>
    [Column("team_manages_staff")]
    public bool TeamManagesStaff { get; set; } = true;

    public List<TeamTenantAccessRoleEntity> AccessRoles { get; set; } = [];

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}

/// <summary>
/// A role a <see cref="TeamTenantAccessEntity"/> gives. The link is stored rather than the role's
/// permissions, which can change later, like <see cref="TenantMemberReasonRoleEntity"/>.
/// </summary>
[Table("team_tenant_access_roles")]
public class TeamTenantAccessRoleEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_tenant_access_id")]
    public Guid TeamTenantAccessId { get; set; }

    public TeamTenantAccessEntity? TeamTenantAccess { get; set; }

    [Column("tenant_role_id")]
    public Guid TenantRoleId { get; set; }

    public TenantRoleEntity? TenantRole { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// Whether a site has approved one staff member of a team that has access to it: one row for each
/// pair, written when the staff member joins or the site approves the team. Not <c>ITenantScoped</c>,
/// like <see cref="TeamTenantAccessEntity"/>; its <c>tenant_pin</c> policy reads the site off the access.
/// </summary>
/// <remarks>
/// Holds no subject: the staff member is read through <see cref="TeamMemberEntity"/>. It does not
/// itself give access to the site.
/// </remarks>
[Table("team_site_staff")]
public class TeamSiteStaffEntity : ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_tenant_access_id")]
    public Guid TeamTenantAccessId { get; set; }

    public TeamTenantAccessEntity? TeamTenantAccess { get; set; }

    [Column("team_member_id")]
    public Guid TeamMemberId { get; set; }

    public TeamMemberEntity? TeamMember { get; set; }

    /// <summary>A <see cref="Nocturne.Core.Models.Identity.TeamSiteStaffStatus"/>.</summary>
    [Required]
    [MaxLength(32)]
    [Column("status")]
    public string Status { get; set; } = string.Empty;

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}

/// <summary>
/// An owner's decline of one subject as staff of a team at a site, for as long as the team's access to
/// the site lasts. Not <c>ITenantScoped</c>, like <see cref="TeamSiteStaffEntity"/>; its <c>tenant_pin</c>
/// policy reads the site off the access.
/// </summary>
/// <remarks>
/// A staff member's <see cref="TeamSiteStaffEntity"/> row goes with their team membership, and the
/// decline must outlast it: a staff member removed and invited again is still declined. So the row's
/// <see cref="Nocturne.Core.Models.Identity.TeamSiteStaffStatus.Declined"/> is what every read, the sync
/// and the switch go by, and this memory is read only when a staff row is made. The owner's decision is
/// the one writer of both. It goes with the access: a site that stops the team and approves it again
/// decides afresh.
/// </remarks>
[Table("team_site_declines")]
public class TeamSiteDeclineEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_tenant_access_id")]
    public Guid TeamTenantAccessId { get; set; }

    public TeamTenantAccessEntity? TeamTenantAccess { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    public SubjectEntity? Subject { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
