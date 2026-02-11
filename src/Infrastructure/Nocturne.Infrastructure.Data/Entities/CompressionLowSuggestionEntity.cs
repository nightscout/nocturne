using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for compression low suggestions
/// </summary>
[Table("compression_low_suggestions")]
public class CompressionLowSuggestionEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Start of detected region (Unix milliseconds)
    /// </summary>
    [Column("start_mills")]
    public long StartMills { get; set; }

    /// <summary>
    /// End of detected region (Unix milliseconds)
    /// </summary>
    [Column("end_mills")]
    public long EndMills { get; set; }

    /// <summary>
    /// Confidence score (0-1)
    /// </summary>
    [Column("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Status: Pending, Accepted, Dismissed
    /// </summary>
    [Column("status")]
    [MaxLength(20)]
    [Required]
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Night this was detected for (date of sleep start)
    /// </summary>
    [Column("night_of")]
    public DateOnly NightOf { get; set; }

    /// <summary>
    /// When created (Unix milliseconds)
    /// </summary>
    [Column("created_at")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// When reviewed (Unix milliseconds, null if pending)
    /// </summary>
    [Column("reviewed_at")]
    public long? ReviewedAt { get; set; }

    /// <summary>
    /// ID of created StateSpan (set when accepted)
    /// </summary>
    [Column("state_span_id")]
    public Guid? StateSpanId { get; set; }

    /// <summary>
    /// Lowest glucose during compression low (mg/dL)
    /// </summary>
    [Column("lowest_glucose")]
    public double? LowestGlucose { get; set; }

    /// <summary>
    /// Max drop rate (mg/dL per minute)
    /// </summary>
    [Column("drop_rate")]
    public double? DropRate { get; set; }

    /// <summary>
    /// Recovery time (minutes)
    /// </summary>
    [Column("recovery_minutes")]
    public int? RecoveryMinutes { get; set; }
}
