using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for InjectableMedication
/// Represents injectable medications like insulin pens, GLP-1 medications, etc.
/// </summary>
[Table("injectable_medications")]
public class InjectableMedicationEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name (e.g., "Humalog", "Ozempic", "Mounjaro")
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Category of medication (stored as string, not enum)
    /// Values: "RapidActingInsulin", "LongActingInsulin", "GLP1", "Other"
    /// </summary>
    [Column("category")]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Concentration (e.g., 100 for U-100 insulin, 200 for U-200)
    /// </summary>
    [Column("concentration")]
    public int Concentration { get; set; }

    /// <summary>
    /// Unit type for dosing (stored as string, not enum)
    /// Values: "Units", "Milligrams", "Milliliters"
    /// </summary>
    [Column("unit_type")]
    [MaxLength(50)]
    public string UnitType { get; set; } = string.Empty;

    /// <summary>
    /// Duration of insulin activity in hours (for insulin types)
    /// </summary>
    [Column("dia")]
    public double? Dia { get; set; }

    /// <summary>
    /// Time to onset of action in hours
    /// </summary>
    [Column("onset")]
    public double? Onset { get; set; }

    /// <summary>
    /// Time to peak effect in hours
    /// </summary>
    [Column("peak")]
    public double? Peak { get; set; }

    /// <summary>
    /// Total duration of effect in hours
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Default dose for quick entry
    /// </summary>
    [Column("default_dose")]
    public double? DefaultDose { get; set; }

    /// <summary>
    /// Display order for sorting in UI
    /// </summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// Whether this medication is archived (hidden from active list)
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
    /// Navigation property for doses administered with this medication
    /// </summary>
    public virtual ICollection<InjectableDoseEntity> Doses { get; set; } = new List<InjectableDoseEntity>();

    /// <summary>
    /// Navigation property for pen/vial tracking
    /// </summary>
    public virtual ICollection<PenVialEntity> PenVials { get; set; } = new List<PenVialEntity>();
}
