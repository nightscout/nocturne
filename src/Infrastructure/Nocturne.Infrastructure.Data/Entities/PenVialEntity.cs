using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for PenVial
/// Tracks individual insulin pens or vials for inventory management
/// </summary>
[Table("pen_vials")]
public class PenVialEntity
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
    /// When the pen/vial was opened, in milliseconds since Unix epoch
    /// </summary>
    [Column("opened_at")]
    public long? OpenedAt { get; set; }

    /// <summary>
    /// When the pen/vial expires, in milliseconds since Unix epoch
    /// </summary>
    [Column("expires_at")]
    public long? ExpiresAt { get; set; }

    /// <summary>
    /// Initial units when the pen/vial was opened
    /// </summary>
    [Column("initial_units")]
    public double? InitialUnits { get; set; }

    /// <summary>
    /// Remaining units in the pen/vial
    /// </summary>
    [Column("remaining_units")]
    public double? RemainingUnits { get; set; }

    /// <summary>
    /// Lot number for tracking purposes
    /// </summary>
    [Column("lot_number")]
    [MaxLength(100)]
    public string? LotNumber { get; set; }

    /// <summary>
    /// Status of the pen/vial (stored as string, not enum)
    /// Values: "Active", "Empty", "Expired", "Discarded"
    /// </summary>
    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "Active";

    /// <summary>
    /// Additional notes about this pen/vial
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether this pen/vial is archived (hidden from active list)
    /// </summary>
    [Column("is_archived")]
    public bool IsArchived { get; set; }

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
    /// Navigation property for doses administered from this pen/vial
    /// </summary>
    public virtual ICollection<InjectableDoseEntity> Doses { get; set; } = new List<InjectableDoseEntity>();
}
