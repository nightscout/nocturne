using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for linked records used in deduplication.
/// Links records from different sources that represent the same underlying event.
/// </summary>
[Table("linked_records")]
public class LinkedRecordEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical group identifier shared by all records representing the same event
    /// </summary>
    [Column("canonical_id")]
    public Guid CanonicalId { get; set; }

    /// <summary>
    /// Type of record being linked: "entry", "treatment", or "statespan"
    /// </summary>
    [Column("record_type")]
    [MaxLength(20)]
    public string RecordType { get; set; } = string.Empty;

    /// <summary>
    /// ID of the linked record (entries.id, treatments.id, or state_spans.id)
    /// </summary>
    [Column("record_id")]
    public Guid RecordId { get; set; }

    /// <summary>
    /// Timestamp from the source record (Mills)
    /// </summary>
    [Column("source_timestamp")]
    public long SourceTimestamp { get; set; }

    /// <summary>
    /// Data source identifier (e.g., "glooko-connector", "mylife-connector")
    /// </summary>
    [Column("data_source")]
    [MaxLength(100)]
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the primary record in the canonical group (earliest timestamp)
    /// </summary>
    [Column("is_primary")]
    public bool IsPrimary { get; set; }

    /// <summary>
    /// When this link was created
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
