using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for patient insulin records (rapid-acting, long-acting, etc.)
/// Maps to Nocturne.Core.Models.V4.PatientInsulin
/// </summary>
[Table("patient_insulins")]
public class PatientInsulinEntity : ITenantScoped, ISoftDeletable, ISystemTimestamped
{
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
    /// Category of insulin stored as string (e.g. "RapidActing", "LongActing")
    /// </summary>
    [Column("insulin_category")]
    [MaxLength(32)]
    public string InsulinCategory { get; set; } = string.Empty;

    /// <summary>
    /// Insulin name (e.g. "Humalog", "Lantus")
    /// </summary>
    [Column("name")]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Date the insulin was started
    /// </summary>
    [Column("start_date")]
    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Date the insulin was stopped
    /// </summary>
    [Column("end_date")]
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Whether this insulin is currently in use
    /// </summary>
    [Column("is_current")]
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Free-text notes about the insulin
    /// </summary>
    [Column("notes")]
    [MaxLength(4096)]
    public string? Notes { get; set; }

    /// <summary>
    /// Reference to a specific insulin formulation definition
    /// </summary>
    [Column("formulation_id")]
    [MaxLength(64)]
    public string? FormulationId { get; set; }

    /// <summary>
    /// Duration of insulin action in hours
    /// </summary>
    [Column("dia")]
    public double Dia { get; set; } = 4.0;

    /// <summary>
    /// Peak activity time in minutes
    /// </summary>
    [Column("peak")]
    public int Peak { get; set; } = 75;

    /// <summary>
    /// Insulin activity curve type (e.g. "rapid-acting", "ultra-rapid")
    /// </summary>
    [Column("curve")]
    [MaxLength(32)]
    public string Curve { get; set; } = "rapid-acting";

    /// <summary>
    /// Insulin concentration in units/mL (e.g. 100 for U-100)
    /// </summary>
    [Column("concentration")]
    public int Concentration { get; set; } = 100;

    /// <summary>
    /// Role of the insulin: Bolus, Basal, or Both
    /// </summary>
    [Column("role")]
    [MaxLength(16)]
    public string Role { get; set; } = "Both";

    /// <summary>
    /// Whether this is the primary insulin for its role
    /// </summary>
    [Column("is_primary")]
    public bool IsPrimary { get; set; }

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
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
