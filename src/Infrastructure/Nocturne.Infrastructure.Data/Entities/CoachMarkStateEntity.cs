using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for per-user coach mark progression. Maps to the coach_mark_states table.
/// </summary>
[Table("coach_mark_states")]
public class CoachMarkStateEntity : ITenantScoped
{
    /// <summary>
    /// The unique identifier of the tenant this coach mark state belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Unique identifier for the coach mark state record
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// The subject (user) this coach mark state applies to
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// The key identifying which coach mark this state tracks
    /// </summary>
    [Column("mark_key")]
    [MaxLength(255)]
    public string MarkKey { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the coach mark (e.g. unseen, seen, completed)
    /// </summary>
    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "unseen";

    /// <summary>
    /// When the coach mark was first seen by the user
    /// </summary>
    [Column("seen_at")]
    public DateTime? SeenAt { get; set; }

    /// <summary>
    /// When the coach mark was completed (dismissed) by the user
    /// </summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
}
