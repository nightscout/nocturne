using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for HeartRate records
/// Maps to Nocturne.Core.Models.HeartRate
/// </summary>
[Table("heart_rates")]
public class HeartRateEntity : ITenantScoped, ISoftDeletable, ISyncDedupable
{
    /// <summary>
    /// Identifier of the tenant this heart rate record belongs to
    /// </summary>
    /// <summary>
    /// The unique identifier of the tenant this record belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId as string for reference/migration tracking
    /// </summary>
    [Column("original_id")]
    [MaxLength(24)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC DateTime (timestamptz)
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Heart rate in beats per minute
    /// </summary>
    [Column("bpm")]
    public int Bpm { get; set; }

    /// <summary>
    /// Accuracy of the heart rate reading
    /// </summary>
    [Column("accuracy")]
    public int Accuracy { get; set; }

    /// <summary>
    /// Device identifier that recorded this reading
    /// </summary>
    [Column("device")]
    [MaxLength(255)]
    public string? Device { get; set; }

    /// <summary>
    /// Who entered this record
    /// </summary>
    [Column("entered_by")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Origin data source identifier; first half of the sync-dedup key.
    /// </summary>
    [Column("data_source")]
    [MaxLength(256)]
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier; second half of the sync-dedup key. A create
    /// matching an existing (DataSource, SyncIdentifier) row updates it in place.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is hidden by the global query filter.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
