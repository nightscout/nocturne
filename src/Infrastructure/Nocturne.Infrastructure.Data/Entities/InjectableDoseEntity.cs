using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for InjectableDose
/// Represents a single dose/injection of an injectable medication
/// </summary>
[Table("injectable_doses")]
public class InjectableDoseEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the injectable medication
    /// </summary>
    [Column("injectable_medication_id")]
    public Guid InjectableMedicationId { get; set; }

    /// <summary>
    /// Amount of medication delivered (in units appropriate for the medication type)
    /// </summary>
    [Column("units")]
    public double Units { get; set; }

    /// <summary>
    /// Time of injection in milliseconds since Unix epoch
    /// </summary>
    [Column("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// Body location where injection was administered (stored as string, not enum)
    /// Values: "Abdomen", "Arm", "Thigh", "Buttock", "Other"
    /// </summary>
    [Column("injection_site")]
    [MaxLength(50)]
    public string? InjectionSite { get; set; }

    /// <summary>
    /// Foreign key to the pen/vial used (optional)
    /// </summary>
    [Column("pen_vial_id")]
    public Guid? PenVialId { get; set; }

    /// <summary>
    /// Lot number for tracking purposes
    /// </summary>
    [Column("lot_number")]
    [MaxLength(100)]
    public string? LotNumber { get; set; }

    /// <summary>
    /// Additional notes about this dose
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Who entered this dose
    /// </summary>
    [Column("entered_by")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Data source identifier indicating where this dose originated from
    /// </summary>
    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    /// <summary>
    /// Original ID for migration/import tracking
    /// </summary>
    [Column("original_id")]
    [MaxLength(255)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [Column("sys_created_at")]
    public DateTimeOffset SysCreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTimeOffset SysUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation properties

    /// <summary>
    /// Navigation property to the injectable medication
    /// </summary>
    public virtual InjectableMedicationEntity InjectableMedication { get; set; } = null!;

    /// <summary>
    /// Navigation property to the pen/vial used (optional)
    /// </summary>
    public virtual PenVialEntity? PenVial { get; set; }
}
