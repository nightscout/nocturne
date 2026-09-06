using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for an individual sleep stage within a session.
/// Maps to Nocturne.Core.Models.SleepStage.
/// </summary>
[Table("sleep_stages")]
public class SleepStageEntity : ITenantScoped
{
    /// <summary>
    /// The unique identifier of the tenant this record belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent sleep session.
    /// </summary>
    [Column("sleep_session_id")]
    public Guid SleepSessionId { get; set; }

    /// <summary>
    /// When this stage started (UTC).
    /// </summary>
    [Column("start_time")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When this stage ended (UTC).
    /// </summary>
    [Column("end_time")]
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Sleep stage type (e.g. "Deep", "Light", "Rem", "Awake").
    /// </summary>
    [Column("stage")]
    [MaxLength(20)]
    [Required]
    public string Stage { get; set; } = string.Empty;

    /// <summary>
    /// Zero-based position of this stage within the session's stage sequence.
    /// </summary>
    [Column("ordinal")]
    public int Ordinal { get; set; }

    /// <summary>
    /// Navigation to the parent sleep session.
    /// </summary>
    [ForeignKey(nameof(SleepSessionId))]
    public SleepSessionEntity SleepSession { get; set; } = null!;
}
