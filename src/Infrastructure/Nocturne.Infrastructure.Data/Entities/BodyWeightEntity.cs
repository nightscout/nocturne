using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Body composition measurement recorded by a scale or manual entry.
/// </summary>
[Table("body_weights")]
public class BodyWeightEntity : ITenantScoped, ISoftDeletable, ISyncDedupable, ISystemTimestamped
{
    /// <summary>Owning tenant for RLS isolation.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Legacy MongoDB ObjectId preserved during migration.</summary>
    [Column("original_id")]
    [MaxLength(24)]
    public string? OriginalId { get; set; }

    /// <summary>Unix-epoch milliseconds when the measurement was taken (source of truth).</summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>Body weight in kilograms.</summary>
    [Column("weight_kg")]
    public decimal WeightKg { get; set; }

    /// <summary>Body fat as a percentage, if reported by the device.</summary>
    [Column("body_fat_percent")]
    public decimal? BodyFatPercent { get; set; }

    /// <summary>Lean (non-fat) mass in kilograms, if reported by the device.</summary>
    [Column("lean_mass_kg")]
    public decimal? LeanMassKg { get; set; }

    /// <summary>Identifier of the device or app that captured the measurement.</summary>
    [Column("device")]
    [MaxLength(255)]
    public string? Device { get; set; }

    /// <summary>User or application that created this record.</summary>
    [Column("entered_by")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>Client-supplied ISO-8601 creation timestamp string.</summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>UTC offset in minutes at the time of measurement.</summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>Origin data source identifier; first half of the sync-dedup key.</summary>
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

    /// <summary>Server-side row creation timestamp.</summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Server-side row last-update timestamp.</summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is hidden by the global query filter.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
