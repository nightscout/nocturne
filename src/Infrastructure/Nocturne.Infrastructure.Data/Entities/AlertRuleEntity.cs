using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for AlertRule (notification rules)
/// Maps to notification alert rule configuration
/// </summary>
[Table("alert_rules")]
public class AlertRuleEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID for alert rule
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this rule belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the alert rule
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this alert rule is currently enabled
    /// </summary>
    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Low glucose threshold in mg/dL
    /// </summary>
    [Column("low_threshold")]
    public decimal? LowThreshold { get; set; }

    /// <summary>
    /// High glucose threshold in mg/dL
    /// </summary>
    [Column("high_threshold")]
    public decimal? HighThreshold { get; set; }

    /// <summary>
    /// Urgent low glucose threshold in mg/dL
    /// </summary>
    [Column("urgent_low_threshold")]
    public decimal? UrgentLowThreshold { get; set; }

    /// <summary>
    /// Urgent high glucose threshold in mg/dL
    /// </summary>
    [Column("urgent_high_threshold")]
    public decimal? UrgentHighThreshold { get; set; }

    /// <summary>
    /// Active hours configuration (stored as JSON)
    /// </summary>
    [Column("active_hours", TypeName = "jsonb")]
    public string? ActiveHours { get; set; }

    /// <summary>
    /// Days of week when rule is active (JSON array)
    /// </summary>
    [Column("days_of_week", TypeName = "jsonb")]
    public string? DaysOfWeek { get; set; }

    /// <summary>
    /// Notification channels configuration (stored as JSON)
    /// </summary>
    [Column("notification_channels", TypeName = "jsonb")]
    public string NotificationChannels { get; set; } = "[]";

    /// <summary>
    /// Delay in minutes before escalating alert
    /// </summary>
    [Column("escalation_delay_minutes")]
    public int EscalationDelayMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum number of escalations before stopping
    /// </summary>
    [Column("max_escalations")]
    public int MaxEscalations { get; set; } = 3;

    /// <summary>
    /// Default snooze duration in minutes
    /// </summary>
    [Column("default_snooze_minutes")]
    public int DefaultSnoozeMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum allowed snooze duration in minutes
    /// </summary>
    [Column("max_snooze_minutes")]
    public int MaxSnoozeMinutes { get; set; } = 120;

    /// <summary>
    /// Cooldown period in minutes after an alert is resolved before it can trigger again.
    /// </summary>
    [Column("cooldown_minutes")]
    public int CooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Lead time in minutes for forecast alerts.
    /// </summary>
    [Column("forecast_lead_time_minutes")]
    public int? ForecastLeadTimeMinutes { get; set; }

    /// <summary> Full JSON configuration from the client (audio, visual, etc) </summary>
    [Column("client_configuration", TypeName = "jsonb")]
    public string? ClientConfiguration { get; set; }

    /// <summary>
    /// When this alert rule was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this alert rule was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property for alert history
    /// </summary>
    public virtual ICollection<AlertHistoryEntity> AlertHistory { get; set; } =
        new List<AlertHistoryEntity>();
}
