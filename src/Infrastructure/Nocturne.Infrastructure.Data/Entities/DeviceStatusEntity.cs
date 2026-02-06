using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for DeviceStatus
/// Maps to Nocturne.Core.Models.DeviceStatus
/// </summary>
[Table("devicestatus")]
public class DeviceStatusEntity : IHasSysCreatedAt, IHasSysUpdatedAt
{
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
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utcOffset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device name that submitted this status
    /// </summary>
    [Column("device")]
    [MaxLength(255)]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Whether the device is currently charging
    /// </summary>
    [Column("isCharging")]
    public bool? IsCharging { get; set; }

    /// <summary>
    /// Uploader status information (stored as JSON)
    /// </summary>
    [Column("uploader", TypeName = "jsonb")]
    public string? UploaderJson { get; set; }

    /// <summary>
    /// Pump status information (stored as JSON)
    /// </summary>
    [Column("pump", TypeName = "jsonb")]
    public string? PumpJson { get; set; }

    /// <summary>
    /// OpenAPS status information (stored as JSON)
    /// </summary>
    [Column("openaps", TypeName = "jsonb")]
    public string? OpenApsJson { get; set; }

    /// <summary>
    /// Loop status information (stored as JSON)
    /// </summary>
    [Column("loop", TypeName = "jsonb")]
    public string? LoopJson { get; set; }

    /// <summary>
    /// xDrip+ status information (stored as JSON)
    /// </summary>
    [Column("xdripjs", TypeName = "jsonb")]
    public string? XDripJsJson { get; set; }

    /// <summary>
    /// Radio adapter information (stored as JSON)
    /// </summary>
    [Column("radioAdapter", TypeName = "jsonb")]
    public string? RadioAdapterJson { get; set; }

    /// <summary>
    /// MM Connect status information (stored as JSON)
    /// </summary>
    [Column("connect", TypeName = "jsonb")]
    public string? ConnectJson { get; set; }

    /// <summary>
    /// Override status information (stored as JSON)
    /// </summary>
    [Column("override", TypeName = "jsonb")]
    public string? OverrideJson { get; set; }

    /// <summary>
    /// CGM status information (stored as JSON)
    /// </summary>
    [Column("cgm", TypeName = "jsonb")]
    public string? CgmJson { get; set; }

    /// <summary>
    /// Blood glucose meter status information (stored as JSON)
    /// </summary>
    [Column("meter", TypeName = "jsonb")]
    public string? MeterJson { get; set; }

    /// <summary>
    /// Insulin pen status information (stored as JSON)
    /// </summary>
    [Column("insulinPen", TypeName = "jsonb")]
    public string? InsulinPenJson { get; set; }

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
    /// Additional properties from import (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}
