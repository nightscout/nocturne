using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A named group inside a team, holding some of the sites it follows and some of its staff. It has no end.
/// Not tenant-scoped. Its <c>subject_pin</c> policy lets a context pinned to a subject read every group of the
/// teams they manage and the groups they are in as staff, and write only those of the teams they manage.
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
/// stopped and given again keeps its place. Not <c>ITenantScoped</c>: the team reads it across sites. Its
/// <c>subject_pin</c> policy reads as the group's, and a restrictive <c>tenant_pin</c> lets a context pinned to
/// a tenant see and write only that tenant's rows.
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
/// Not tenant-scoped. Its <c>subject_pin</c> policy lets a context pinned to a subject read the places of the
/// teams they manage and their own place, and write only those of the teams they manage.
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

    /// <summary>
    /// The group's team, held here so that this table's policy does not read <c>team_groups</c>, whose own
    /// policy reads this table to let a staff member see the groups they are in. PostgreSQL refuses policies
    /// that read each other's tables.
    /// </summary>
    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

    [Column("team_member_id")]
    public Guid TeamMemberId { get; set; }

    public TeamMemberEntity? TeamMember { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
