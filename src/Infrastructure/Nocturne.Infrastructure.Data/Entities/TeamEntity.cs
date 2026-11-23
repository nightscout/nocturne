using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Identity;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A team: people who run something together, above any one tenant. Not tenant-scoped. Its
/// <c>subject_pin</c> policy shows a pinned context only the teams its subject is a member of.
/// </summary>
/// <remarks>
/// The name is not unique: an index on it would tell anyone creating a team that one of that name exists.
/// </remarks>
[Table("teams")]
public class TeamEntity : ISystemTimestamped
{
    public const int NameMaxLength = 100;

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(NameMaxLength)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Who created the team, or null once that subject has been deleted.</summary>
    [Column("created_by_subject_id")]
    public Guid? CreatedBySubjectId { get; set; }

    public SubjectEntity? CreatedBy { get; set; }

    public List<TeamMemberEntity> Members { get; set; } = [];

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}

/// <summary>
/// A subject's place in a team. Not tenant-scoped; the <c>subject_pin</c> policy shows a pinned
/// context only its subject's rows.
/// </summary>
[Table("team_members")]
public class TeamMemberEntity : ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    public SubjectEntity? Subject { get; set; }

    /// <summary>A <see cref="TeamMemberKind"/>.</summary>
    [Required]
    [MaxLength(32)]
    [Column("kind")]
    public string Kind { get; set; } = TeamMemberKind.Manager;

    /// <summary>
    /// A staff member's ceiling: a <c>RoleSeeds</c> slug, since roles are per site; null for everything
    /// the team holds at a site. Null for a manager.
    /// </summary>
    [MaxLength(64)]
    [Column("role_ceiling_slug")]
    public string? RoleCeilingSlug { get; set; }

    /// <summary>Whether a staff member reads only the last 24 hours. False for a manager.</summary>
    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

    /// <summary>When a staff member's place ends, in UTC; null for no end. Null for a manager.</summary>
    [Column("ends_at")]
    public DateTime? EndsAt { get; set; }

    /// <summary>The staff invite the member joined by, or null once it is deleted. Null for a manager.</summary>
    [Column("created_from_staff_invite_id")]
    public Guid? CreatedFromStaffInviteId { get; set; }

    public TeamStaffInviteEntity? CreatedFromStaffInvite { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}

/// <summary>
/// What every kind of team link has: a manager invite, a staff invite and an access link live and die
/// by the same columns.
/// </summary>
public interface ITeamLink
{
    Guid Id { get; }

    Guid TeamId { get; }

    Guid CreatedBySubjectId { get; }

    DateTime ExpiresAt { get; }

    int? MaxUses { get; }

    int UseCount { get; }

    DateTime? RevokedAt { get; set; }
}

/// <summary>
/// A link that makes whoever opens it, signed in, a member of a team. Not tenant-scoped, like
/// <see cref="TenantCreationInviteEntity"/>.
/// </summary>
[Table("team_invites")]
public class TeamInviteEntity : ISystemCreated, ITeamLink
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("team_id")]
    public Guid TeamId { get; set; }

    public TeamEntity? Team { get; set; }

    /// <summary>A <see cref="TeamMemberKind"/>: what accepting the invite makes the subject.</summary>
    [Required]
    [MaxLength(32)]
    [Column("kind")]
    public string Kind { get; set; } = TeamMemberKind.Manager;

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

    /// <summary>How many people the link may add; null for no cap.</summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    [Column("use_count")]
    public int UseCount { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// A link that makes whoever opens it, signed in, a staff member of a team. Not tenant-scoped, like
/// <see cref="TeamInviteEntity"/>, and kept apart from it so that neither token can be redeemed as the
/// other.
/// </summary>
[Table("team_staff_invites")]
public class TeamStaffInviteEntity : ISystemCreated, ITeamLink
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

    /// <summary>How many people the link may add; null for no cap.</summary>
    [Column("max_uses")]
    public int? MaxUses { get; set; }

    [Column("use_count")]
    public int UseCount { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>The ceiling the staff member joins with; see <see cref="TeamMemberEntity.RoleCeilingSlug"/>.</summary>
    [MaxLength(64)]
    [Column("role_ceiling_slug")]
    public string? RoleCeilingSlug { get; set; }

    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

    /// <summary>When the staff member's place ends, in UTC; null for no end.</summary>
    [Column("staff_ends_at")]
    public DateTime? StaffEndsAt { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
