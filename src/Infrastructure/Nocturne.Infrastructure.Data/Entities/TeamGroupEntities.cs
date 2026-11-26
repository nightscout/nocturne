using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A named group inside a team, holding some of the sites it follows and some of its staff. It has no end.
/// Not tenant-scoped; its <c>subject_pin</c> policy is the same as <c>teams</c>': a context pinned to a subject
/// sees only the groups of the teams the subject is a member of, and writes only those of the teams they manage.
/// </summary>
/// <remarks>
/// While the team narrows to groups (<see cref="Nocturne.Core.Models.Identity.TeamStaffReach.TheirGroups"/>),
/// a staff member reaches a site only while both are in one of its groups (<see cref="Services.TeamReasons.GivesReason"/>).
/// The people and the staff are kept in two tables, since one is keyed by a site and the other by a place in
/// the team, and each goes with what it names.
/// </remarks>
[Table("team_groups")]
public class TeamGroupEntity : ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

    [Required]
    [MaxLength(TeamEntity.NameMaxLength)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The name in lower case, computed by the database, which the team's unique index is over.</summary>
    [Column("name_key")]
    public string NameKey { get; set; } = string.Empty;

    public List<TeamGroupPersonEntity> People { get; set; } = [];

    public List<TeamGroupStaffEntity> Staff { get; set; } = [];

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}

/// <summary>
/// A site in a team's group. Keyed by the site, never by the team's access to it, so a site whose access is
/// stopped and given again keeps its place. Not <c>ITenantScoped</c>: the team reads it across sites. It has
/// the group's <c>subject_pin</c> policy and a restrictive <c>tenant_pin</c>, so a context pinned to a tenant
/// sees and writes only that tenant's rows.
/// </summary>
[Table("team_group_people")]
public class TeamGroupPersonEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_group_id")]
    public Guid TeamGroupId { get; set; }

    public TeamGroupEntity? TeamGroup { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    public TenantEntity? Tenant { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// A staff member in a team's group, by their place in the team, which takes it with it when they leave.
/// Not tenant-scoped; it has the group's <c>subject_pin</c> policy.
/// </summary>
[Table("team_group_staff")]
public class TeamGroupStaffEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_group_id")]
    public Guid TeamGroupId { get; set; }

    public TeamGroupEntity? TeamGroup { get; set; }

    [Column("team_member_id")]
    public Guid TeamMemberId { get; set; }

    public TeamMemberEntity? TeamMember { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
