using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NotificationPreferences (user notification settings)
/// Maps to user-specific notification preferences and delivery settings
/// </summary>
[Table("notification_preferences")]
public class NotificationPreferencesEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID for notification preferences
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier these preferences belong to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Whether email notifications are enabled
    /// </summary>
    [Column("email_enabled")]
    public bool EmailEnabled { get; set; } = true;

    /// <summary>
    /// Email address for notifications
    /// </summary>
    [Column("email_address")]
    [MaxLength(255)]
    public string? EmailAddress { get; set; }

    /// <summary>
    /// Whether Pushover notifications are enabled
    /// </summary>
    [Column("pushover_enabled")]
    public bool PushoverEnabled { get; set; } = false;

    /// <summary>
    /// Pushover user key for notifications
    /// </summary>
    [Column("pushover_user_key")]
    [MaxLength(255)]
    public string? PushoverUserKey { get; set; }

    /// <summary>
    /// Pushover devices configuration (stored as JSON)
    /// </summary>
    [Column("pushover_devices", TypeName = "jsonb")]
    public string? PushoverDevices { get; set; }

    /// <summary>
    /// Whether SMS notifications are enabled
    /// </summary>
    [Column("sms_enabled")]
    public bool SmsEnabled { get; set; } = false;

    /// <summary>
    /// Phone number for SMS notifications
    /// </summary>
    [Column("sms_phone_number")]
    [MaxLength(50)]
    public string? SmsPhoneNumber { get; set; }

    /// <summary>
    /// Whether webhook notifications are enabled
    /// </summary>
    [Column("webhook_enabled")]
    public bool WebhookEnabled { get; set; } = false;

    /// <summary>
    /// Webhook URLs configuration (stored as JSON)
    /// </summary>
    [Column("webhook_urls", TypeName = "jsonb")]
    public string? WebhookUrls { get; set; }

    /// <summary>
    /// Start time for quiet hours (no notifications)
    /// </summary>
    [Column("quiet_hours_start")]
    public TimeOnly? QuietHoursStart { get; set; }

    /// <summary>
    /// End time for quiet hours (no notifications)
    /// </summary>
    [Column("quiet_hours_end")]
    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>
    /// Whether quiet hours are enabled
    /// </summary>
    [Column("quiet_hours_enabled")]
    public bool QuietHoursEnabled { get; set; } = false;

    /// <summary>
    /// Whether emergency alerts override quiet hours
    /// </summary>
    [Column("emergency_override_quiet_hours")]
    public bool EmergencyOverrideQuietHours { get; set; } = true;

    /// <summary>
    /// Whether push notifications are enabled
    /// </summary>
    [Column("push_enabled")]
    public bool PushEnabled { get; set; } = true;

    /// <summary>
    /// Battery low threshold percentage for device alerts
    /// </summary>
    [Column("battery_low_threshold")]
    public int? BatteryLowThreshold { get; set; }

    /// <summary>
    /// Hours before sensor expiration to send warning
    /// </summary>
    [Column("sensor_expiration_warning_hours")]
    public int? SensorExpirationWarningHours { get; set; }

    /// <summary>
    /// Minutes of data gap before warning
    /// </summary>
    [Column("data_gap_warning_minutes")]
    public int? DataGapWarningMinutes { get; set; }

    /// <summary>
    /// Hours between calibration reminders
    /// </summary>
    [Column("calibration_reminder_hours")]
    public int? CalibrationReminderHours { get; set; }

    /// <summary>
    /// When these preferences were created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When these preferences were last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
