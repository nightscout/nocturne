using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A request from an authenticated user to join a tenant they are not yet a member of.
/// </summary>
[Table("membership_requests")]
public class MembershipRequestEntity : ITenantScoped
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// The subject (user) requesting membership.
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Freeform message from the requester identifying themselves to the tenant owner.
    /// </summary>
    [Column("message")]
    [MaxLength(500)]
    public string? Message { get; set; }

    /// <summary>
    /// Current status: pending, approved, denied.
    /// </summary>
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// The subject who approved or denied this request.
    /// </summary>
    [Column("decided_by_subject_id")]
    public Guid? DecidedBySubjectId { get; set; }

    /// <summary>
    /// When the decision was made.
    /// </summary>
    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// Role IDs assigned on approval.
    /// </summary>
    [Column("role_ids", TypeName = "jsonb")]
    public List<Guid>? RoleIds { get; set; }

    /// <summary>
    /// When the request was created.
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
