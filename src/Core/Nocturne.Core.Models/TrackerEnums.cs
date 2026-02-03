using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Category of tracker for grouping and UI filtering
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackerCategory>))]
public enum TrackerCategory
{
    /// <summary>
    /// Consumable devices: cannulas, sensors, infusion sets
    /// </summary>
    Consumable,

    /// <summary>
    /// Insulin reservoirs and cartridges
    /// </summary>
    Reservoir,

    /// <summary>
    /// Appointments: doctor visits, educator sessions, blood tests
    /// </summary>
    Appointment,

    /// <summary>
    /// General reminders: prescriptions, refills, etc.
    /// </summary>
    Reminder,

    /// <summary>
    /// User-defined custom category
    /// </summary>
    Custom,

    /// <summary>
    /// CGM Sensor: continuous glucose monitor sensor
    /// </summary>
    Sensor,

    /// <summary>
    /// Cannula/Infusion Site: insulin pump cannula or infusion site
    /// </summary>
    Cannula,

    /// <summary>
    /// Battery: pump or CGM battery
    /// </summary>
    Battery
}

/// <summary>
/// Reason for completing/ending a tracker instance
/// Category-aware: different reasons apply to different tracker types
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompletionReason>))]
public enum CompletionReason
{
    // === General (all categories) ===

    /// <summary>
    /// Normal completion at or near expected time
    /// </summary>
    Completed,

    /// <summary>
    /// Ran past expected lifespan
    /// </summary>
    Expired,

    /// <summary>
    /// Free-form reason, see CompletionNotes
    /// </summary>
    Other,

    // === Consumable-specific ===

    /// <summary>
    /// Device/sensor failure (error codes, malfunction)
    /// </summary>
    Failed,

    /// <summary>
    /// Physical detachment from body
    /// </summary>
    FellOff,

    /// <summary>
    /// Replaced before threshold (discomfort, site issues, travel)
    /// </summary>
    ReplacedEarly,

    // === Reservoir-specific ===

    /// <summary>
    /// Ran out of insulin
    /// </summary>
    Empty,

    /// <summary>
    /// Refilled or replaced cartridge
    /// </summary>
    Refilled,

    // === Appointment-specific ===

    /// <summary>
    /// Appointment completed successfully
    /// </summary>
    Attended,

    /// <summary>
    /// Appointment moved to different date
    /// </summary>
    Rescheduled,

    /// <summary>
    /// Appointment cancelled entirely
    /// </summary>
    Cancelled,

    /// <summary>
    /// Failed to attend appointment
    /// </summary>
    Missed
}

/// <summary>
/// Urgency level for tracker notification thresholds
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationUrgency>))]
public enum NotificationUrgency
{
    /// <summary>
    /// Informational notification
    /// </summary>
    Info,

    /// <summary>
    /// Warning level notification
    /// </summary>
    Warn,

    /// <summary>
    /// Hazard level notification (more urgent than warning)
    /// </summary>
    Hazard,

    /// <summary>
    /// Urgent notification (highest priority)
    /// </summary>
    Urgent
}

/// <summary>
/// Dashboard visibility threshold for tracker pills
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardVisibility>))]
public enum DashboardVisibility
{
    /// <summary>
    /// Never show on dashboard
    /// </summary>
    Off,

    /// <summary>
    /// Always show on dashboard
    /// </summary>
    Always,

    /// <summary>
    /// Show when age reaches info threshold
    /// </summary>
    Info,

    /// <summary>
    /// Show when age reaches warn threshold
    /// </summary>
    Warn,

    /// <summary>
    /// Show when age reaches hazard threshold
    /// </summary>
    Hazard,

    /// <summary>
    /// Show when age reaches urgent threshold
    /// </summary>
    Urgent
}

/// <summary>
/// Mode of tracker operation
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackerMode>))]
public enum TrackerMode
{
    /// <summary>
    /// Duration-based tracker - runs from StartedAt for LifespanHours
    /// </summary>
    Duration,

    /// <summary>
    /// Event-based tracker - occurs at ScheduledAt datetime
    /// </summary>
    Event
}

/// <summary>
/// How a bolus was calculated/initiated by an APS system
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CalculationType>))]
public enum CalculationType
{
    /// <summary>
    /// User requested, system calculated recommended dose
    /// </summary>
    Suggested,

    /// <summary>
    /// User entered amount directly without system calculation
    /// </summary>
    Manual,

    /// <summary>
    /// AID system initiated automatically (auto-bolus)
    /// </summary>
    Automatic
}

/// <summary>
/// Type of in-app notification for the unified notification system
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InAppNotificationType>))]
public enum InAppNotificationType
{
    /// <summary>
    /// Admin notification for pending password reset request
    /// </summary>
    PasswordResetRequest,

    /// <summary>
    /// Notification that a tracker has not been configured/started
    /// </summary>
    UnconfiguredTracker,

    /// <summary>
    /// Alert triggered by a tracker reaching a notification threshold
    /// </summary>
    TrackerAlert,

    /// <summary>
    /// Daily or periodic statistics summary notification
    /// </summary>
    StatisticsSummary,

    /// <summary>
    /// Response to a user help or feedback request
    /// </summary>
    HelpResponse,

    /// <summary>
    /// Admin notification for pending anonymous login request
    /// </summary>
    AnonymousLoginRequest,

    /// <summary>
    /// Prediction of upcoming low glucose event
    /// </summary>
    PredictedLow,

    /// <summary>
    /// Suggested meal match from connector food entries
    /// </summary>
    SuggestedMealMatch,

    /// <summary>
    /// Suggested tracker reset based on detected events (Site Change treatment or sensor warmup)
    /// </summary>
    SuggestedTrackerMatch,

    /// <summary>
    /// Pending compression low suggestions for user review
    /// </summary>
    CompressionLowReview
}

/// <summary>
/// Reason why a notification was archived
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationArchiveReason>))]
public enum NotificationArchiveReason
{
    /// <summary>
    /// User completed the action associated with the notification
    /// </summary>
    Completed,

    /// <summary>
    /// User dismissed the notification without completing the action
    /// </summary>
    Dismissed,

    /// <summary>
    /// Automatic resolution condition was met (e.g., glucose returned to range)
    /// </summary>
    ConditionMet,

    /// <summary>
    /// Notification expired based on its configured expiration time
    /// </summary>
    Expired
}
