namespace Nocturne.Core.Models;

/// <summary>
/// Category of tracker for grouping and UI filtering
/// </summary>
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
    Custom
}

/// <summary>
/// Reason for completing/ending a tracker instance
/// Category-aware: different reasons apply to different tracker types
/// </summary>
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
