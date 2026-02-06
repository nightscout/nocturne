using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Device Health tracking
/// Comprehensive device monitoring for CGM sensors, insulin pumps, and blood glucose meters
/// </summary>
[Table("device_health")]
public class DeviceHealthEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID for device health record
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this device belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Unique device identifier
    /// </summary>
    [Column("device_id")]
    [MaxLength(255)]
    public string DeviceId { get; set; } = string.Empty;

    // Device identification
    /// <summary>
    /// Type of device (CGM, InsulinPump, BGM, Unknown)
    /// </summary>
    [Column("device_type")]
    public DeviceType DeviceType { get; set; }

    /// <summary>
    /// Human-readable device name
    /// </summary>
    [Column("device_name")]
    [MaxLength(255)]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Device manufacturer
    /// </summary>
    [Column("manufacturer")]
    [MaxLength(255)]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Device model
    /// </summary>
    [Column("model")]
    [MaxLength(255)]
    public string? Model { get; set; }

    /// <summary>
    /// Device serial number
    /// </summary>
    [Column("serial_number")]
    [MaxLength(255)]
    public string? SerialNumber { get; set; }

    // Health metrics
    /// <summary>
    /// Current battery level percentage (0-100)
    /// </summary>
    [Column("battery_level")]
    public decimal? BatteryLevel { get; set; }

    /// <summary>
    /// When the sensor expires (for CGM devices)
    /// </summary>
    [Column("sensor_expiration")]
    public DateTime? SensorExpiration { get; set; }

    /// <summary>
    /// When the device was last calibrated
    /// </summary>
    [Column("last_calibration")]
    public DateTime? LastCalibration { get; set; }

    /// <summary>
    /// When data was last received from this device
    /// </summary>
    [Column("last_data_received")]
    public DateTime? LastDataReceived { get; set; }

    /// <summary>
    /// When the last maintenance alert was sent
    /// </summary>
    [Column("last_maintenance_alert")]
    public DateTime? LastMaintenanceAlert { get; set; }

    // Warning thresholds
    /// <summary>
    /// Battery warning threshold percentage (default: 20%)
    /// </summary>
    [Column("battery_warning_threshold")]
    public decimal BatteryWarningThreshold { get; set; } = 20.0m;

    /// <summary>
    /// Sensor expiration warning time in hours (default: 24 hours)
    /// </summary>
    [Column("sensor_expiration_warning_hours")]
    public int SensorExpirationWarningHours { get; set; } = 24;

    /// <summary>
    /// Data gap warning threshold in minutes (default: 30 minutes)
    /// </summary>
    [Column("data_gap_warning_minutes")]
    public int DataGapWarningMinutes { get; set; } = 30;

    /// <summary>
    /// Calibration reminder interval in hours (default: 12 hours)
    /// </summary>
    [Column("calibration_reminder_hours")]
    public int CalibrationReminderHours { get; set; } = 12;

    // Status tracking
    /// <summary>
    /// Current device status
    /// </summary>
    [Column("status")]
    public DeviceStatusType Status { get; set; }

    /// <summary>
    /// Last error message from the device
    /// </summary>
    [Column("last_error_message")]
    [MaxLength(1000)]
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// When the device status was last updated
    /// </summary>
    [Column("last_status_update")]
    public DateTime? LastStatusUpdate { get; set; }

    /// <summary>
    /// When this device health record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this device health record was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
