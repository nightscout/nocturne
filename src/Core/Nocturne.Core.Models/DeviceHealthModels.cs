using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Device type enumeration
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// Continuous Glucose Monitor
    /// </summary>
    CGM,

    /// <summary>
    /// Insulin Pump
    /// </summary>
    InsulinPump,

    /// <summary>
    /// Blood Glucose Meter
    /// </summary>
    BGM,

    /// <summary>
    /// Unknown device type
    /// </summary>
    Unknown,
}

/// <summary>
/// Device status enumeration
/// </summary>
public enum DeviceStatusType
{
    /// <summary>
    /// Device is active and functioning normally
    /// </summary>
    Active,

    /// <summary>
    /// Device is inactive or not communicating
    /// </summary>
    Inactive,

    /// <summary>
    /// Device has warnings that need attention
    /// </summary>
    Warning,

    /// <summary>
    /// Device has errors that need immediate attention
    /// </summary>
    Error,

    /// <summary>
    /// Device is in maintenance mode
    /// </summary>
    Maintenance,
}

/// <summary>
/// Configuration options for device health monitoring and maintenance alerts
/// </summary>
public class DeviceHealthOptions
{
    /// <summary>
    /// Section name for configuration binding
    /// </summary>
    public const string SectionName = "DeviceHealth";

    /// <summary>
    /// Health check interval in minutes (default: 15 minutes)
    /// </summary>
    public int HealthCheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Data gap warning threshold in minutes (default: 30 minutes)
    /// </summary>
    public int DataGapWarningMinutes { get; set; } = 30;

    /// <summary>
    /// Battery warning threshold percentage (default: 20%)
    /// </summary>
    public int BatteryWarningThreshold { get; set; } = 20;

    /// <summary>
    /// Sensor expiration warning time in hours (default: 24 hours)
    /// </summary>
    public int SensorExpirationWarningHours { get; set; } = 24;

    /// <summary>
    /// Calibration reminder interval in hours (default: 12 hours)
    /// </summary>
    public int CalibrationReminderHours { get; set; } = 12;

    /// <summary>
    /// Maintenance alert cooldown period in hours (default: 4 hours)
    /// </summary>
    public int MaintenanceAlertCooldownHours { get; set; } = 4;

    /// <summary>
    /// Enable predictive alerts based on usage patterns (default: true)
    /// </summary>
    public bool EnablePredictiveAlerts { get; set; } = true;

    /// <summary>
    /// Enable device performance analytics (default: true)
    /// </summary>
    public bool EnablePerformanceAnalytics { get; set; } = true;

    /// <summary>
    /// Maximum number of devices per user (default: 10)
    /// </summary>
    public int MaxDevicesPerUser { get; set; } = 10;

    /// <summary>
    /// Device registration timeout in seconds (default: 30 seconds)
    /// </summary>
    public int DeviceRegistrationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Enable debug logging for device health monitoring (default: false)
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;
}

/// <summary>
/// Device registration request model
/// </summary>
public class DeviceRegistrationRequest
{
    /// <summary>
    /// Unique device identifier
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Type of device (CGM, InsulinPump, BGM, Unknown)
    /// </summary>
    [JsonPropertyName("deviceType")]
    public DeviceType DeviceType { get; set; }

    /// <summary>
    /// Human-readable device name
    /// </summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Device manufacturer
    /// </summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Device model
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Device serial number
    /// </summary>
    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Initial battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public decimal? BatteryLevel { get; set; }

    /// <summary>
    /// Sensor expiration date for CGM devices
    /// </summary>
    [JsonPropertyName("sensorExpiration")]
    public DateTime? SensorExpiration { get; set; }
}

/// <summary>
/// Device health update model
/// </summary>
public class DeviceHealthUpdate
{
    /// <summary>
    /// Current battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public decimal? BatteryLevel { get; set; }

    /// <summary>
    /// When the sensor expires (for CGM devices)
    /// </summary>
    [JsonPropertyName("sensorExpiration")]
    public DateTime? SensorExpiration { get; set; }

    /// <summary>
    /// When the device was last calibrated
    /// </summary>
    [JsonPropertyName("lastCalibration")]
    public DateTime? LastCalibration { get; set; }

    /// <summary>
    /// Device status update
    /// </summary>
    [JsonPropertyName("status")]
    public DeviceStatusType? Status { get; set; }

    /// <summary>
    /// Last error message from the device
    /// </summary>
    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// Additional device-specific data (JSON)
    /// </summary>
    [JsonPropertyName("deviceSpecificData")]
    public Dictionary<string, object>? DeviceSpecificData { get; set; }
}

/// <summary>
/// Device settings update model
/// </summary>
public class DeviceSettingsUpdate
{
    /// <summary>
    /// Battery warning threshold percentage
    /// </summary>
    [JsonPropertyName("batteryWarningThreshold")]
    public decimal? BatteryWarningThreshold { get; set; }

    /// <summary>
    /// Sensor expiration warning time in hours
    /// </summary>
    [JsonPropertyName("sensorExpirationWarningHours")]
    public int? SensorExpirationWarningHours { get; set; }

    /// <summary>
    /// Data gap warning threshold in minutes
    /// </summary>
    [JsonPropertyName("dataGapWarningMinutes")]
    public int? DataGapWarningMinutes { get; set; }

    /// <summary>
    /// Calibration reminder interval in hours
    /// </summary>
    [JsonPropertyName("calibrationReminderHours")]
    public int? CalibrationReminderHours { get; set; }
}



/// <summary>
/// Device issue severity enumeration
/// </summary>
public enum DeviceIssueSeverity
{
    /// <summary>
    /// Low severity, informational
    /// </summary>
    Low,

    /// <summary>
    /// Medium severity, attention recommended
    /// </summary>
    Medium,

    /// <summary>
    /// High severity, action required
    /// </summary>
    High,

    /// <summary>
    /// Critical severity, immediate action required
    /// </summary>
    Critical,
}



/// <summary>
/// Device alert model
/// </summary>
public class DeviceAlert
{
    /// <summary>
    /// Alert identifier
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Device identifier
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// User identifier
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Alert type
    /// </summary>
    [JsonPropertyName("alertType")]
    public DeviceAlertType AlertType { get; set; }

    /// <summary>
    /// Alert severity
    /// </summary>
    [JsonPropertyName("severity")]
    public DeviceIssueSeverity Severity { get; set; }

    /// <summary>
    /// Alert title
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Alert message
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When the alert was triggered
    /// </summary>
    [JsonPropertyName("triggerTime")]
    public DateTime TriggerTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the alert has been acknowledged
    /// </summary>
    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }

    /// <summary>
    /// When the alert was acknowledged
    /// </summary>
    [JsonPropertyName("acknowledgedAt")]
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Additional alert data
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// Device alert type enumeration
/// </summary>
public enum DeviceAlertType
{
    /// <summary>
    /// Battery warning alert
    /// </summary>
    BatteryWarning,

    /// <summary>
    /// Critical battery alert
    /// </summary>
    BatteryCritical,

    /// <summary>
    /// Sensor expiration warning
    /// </summary>
    SensorExpirationWarning,

    /// <summary>
    /// Sensor expired alert
    /// </summary>
    SensorExpired,

    /// <summary>
    /// Calibration reminder alert
    /// </summary>
    CalibrationReminder,

    /// <summary>
    /// Calibration overdue alert
    /// </summary>
    CalibrationOverdue,

    /// <summary>
    /// Data gap detected alert
    /// </summary>
    DataGapDetected,

    /// <summary>
    /// Communication failure alert
    /// </summary>
    CommunicationFailure,

    /// <summary>
    /// Device error alert
    /// </summary>
    DeviceError,

    /// <summary>
    /// Maintenance required alert
    /// </summary>
    MaintenanceRequired,
}



/// <summary>
/// Device health DTO for API operations
/// </summary>
public class DeviceHealth
{
    /// <summary>
    /// Primary key identifier
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this device belongs to
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Unique device identifier
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Type of device (CGM, InsulinPump, BGM, Unknown)
    /// </summary>
    [JsonPropertyName("deviceType")]
    public DeviceType DeviceType { get; set; }

    /// <summary>
    /// Human-readable device name
    /// </summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Device manufacturer
    /// </summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Device model
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Device serial number
    /// </summary>
    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Current battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public decimal? BatteryLevel { get; set; }

    /// <summary>
    /// When the sensor expires (for CGM devices)
    /// </summary>
    [JsonPropertyName("sensorExpiration")]
    public DateTime? SensorExpiration { get; set; }

    /// <summary>
    /// When the device was last calibrated
    /// </summary>
    [JsonPropertyName("lastCalibration")]
    public DateTime? LastCalibration { get; set; }

    /// <summary>
    /// When data was last received from this device
    /// </summary>
    [JsonPropertyName("lastDataReceived")]
    public DateTime? LastDataReceived { get; set; }

    /// <summary>
    /// When the last maintenance alert was sent
    /// </summary>
    [JsonPropertyName("lastMaintenanceAlert")]
    public DateTime? LastMaintenanceAlert { get; set; }

    /// <summary>
    /// Battery warning threshold percentage
    /// </summary>
    [JsonPropertyName("batteryWarningThreshold")]
    public decimal BatteryWarningThreshold { get; set; } = 20.0m;

    /// <summary>
    /// Sensor expiration warning time in hours
    /// </summary>
    [JsonPropertyName("sensorExpirationWarningHours")]
    public int SensorExpirationWarningHours { get; set; } = 24;

    /// <summary>
    /// Data gap warning threshold in minutes
    /// </summary>
    [JsonPropertyName("dataGapWarningMinutes")]
    public int DataGapWarningMinutes { get; set; } = 30;

    /// <summary>
    /// Calibration reminder interval in hours
    /// </summary>
    [JsonPropertyName("calibrationReminderHours")]
    public int CalibrationReminderHours { get; set; } = 12;

    /// <summary>
    /// Current device status
    /// </summary>
    [JsonPropertyName("status")]
    public DeviceStatusType Status { get; set; }

    /// <summary>
    /// Last error message from the device
    /// </summary>
    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// When the device status was last updated
    /// </summary>
    [JsonPropertyName("lastStatusUpdate")]
    public DateTime? LastStatusUpdate { get; set; }

    /// <summary>
    /// When this device health record was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this device health record was last updated
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
