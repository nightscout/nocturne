using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for patient device records (pumps, CGMs, pens, etc.)
/// Maps to Nocturne.Core.Models.V4.PatientDevice
/// </summary>
[Table("patient_devices")]
public class PatientDeviceEntity : ITenantScoped, ISoftDeletable
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
    /// Category of device stored as string (e.g. "InsulinPump", "CGM")
    /// </summary>
    [Column("device_category")]
    [MaxLength(32)]
    public string DeviceCategory { get; set; } = string.Empty;

    /// <summary>
    /// Device manufacturer name
    /// </summary>
    [Column("manufacturer")]
    [MaxLength(256)]
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Device model name
    /// </summary>
    [Column("model")]
    [MaxLength(256)]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Reference to the device catalog entry (e.g. "dexcom-g7")
    /// </summary>
    [Column("catalog_id")]
    [MaxLength(64)]
    public string? CatalogId { get; set; }

    /// <summary>
    /// AID algorithm stored as string (e.g. "OpenAps", "Loop", "ControlIQ")
    /// </summary>
    [Column("aid_algorithm")]
    [MaxLength(32)]
    public string? AidAlgorithm { get; set; }

    /// <summary>
    /// Device serial number
    /// </summary>
    [Column("serial_number")]
    [MaxLength(256)]
    public string? SerialNumber { get; set; }

    /// <summary>
    /// FK to canonical Device record (resolved via serial number)
    /// </summary>
    [Column("device_id")]
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Date the device was started/activated
    /// </summary>
    [Column("start_date")]
    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Date the device was deactivated/replaced
    /// </summary>
    [Column("end_date")]
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Whether this device is currently in use
    /// </summary>
    [Column("is_current")]
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Free-text notes about the device
    /// </summary>
    [Column("notes")]
    [MaxLength(4096)]
    public string? Notes { get; set; }

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
