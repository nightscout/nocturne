using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Semantic color assignments for chart elements.
/// Values are kebab-case strings matching CSS custom property names,
/// so the frontend can resolve colors with: var(--{value})
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChartColor>))]
public enum ChartColor
{
    // Glucose ranges
    [EnumMember(Value = "glucose-very-low")]
    GlucoseVeryLow,

    [EnumMember(Value = "glucose-low")]
    GlucoseLow,

    [EnumMember(Value = "glucose-in-range")]
    GlucoseInRange,

    [EnumMember(Value = "glucose-high")]
    GlucoseHigh,

    [EnumMember(Value = "glucose-very-high")]
    GlucoseVeryHigh,

    // Insulin
    [EnumMember(Value = "insulin-bolus")]
    InsulinBolus,

    [EnumMember(Value = "insulin-basal")]
    InsulinBasal,

    [EnumMember(Value = "insulin-temp-basal")]
    InsulinTempBasal,

    // Carbs
    [EnumMember(Value = "carbs")]
    Carbs,

    // Pump modes
    [EnumMember(Value = "pump-mode-automatic")]
    PumpModeAutomatic,

    [EnumMember(Value = "pump-mode-limited")]
    PumpModeLimited,

    [EnumMember(Value = "pump-mode-manual")]
    PumpModeManual,

    [EnumMember(Value = "pump-mode-boost")]
    PumpModeBoost,

    [EnumMember(Value = "pump-mode-ease-off")]
    PumpModeEaseOff,

    [EnumMember(Value = "pump-mode-sleep")]
    PumpModeSleep,

    [EnumMember(Value = "pump-mode-exercise")]
    PumpModeExercise,

    [EnumMember(Value = "pump-mode-suspended")]
    PumpModeSuspended,

    [EnumMember(Value = "pump-mode-off")]
    PumpModeOff,

    // System events
    [EnumMember(Value = "system-event-alarm")]
    SystemEventAlarm,

    [EnumMember(Value = "system-event-hazard")]
    SystemEventHazard,

    [EnumMember(Value = "system-event-warning")]
    SystemEventWarning,

    [EnumMember(Value = "system-event-info")]
    SystemEventInfo,

    // Activities
    [EnumMember(Value = "activity-sleep")]
    ActivitySleep,

    [EnumMember(Value = "activity-exercise")]
    ActivityExercise,

    [EnumMember(Value = "activity-illness")]
    ActivityIllness,

    [EnumMember(Value = "activity-travel")]
    ActivityTravel,

    // Trackers
    [EnumMember(Value = "tracker-sensor")]
    TrackerSensor,

    [EnumMember(Value = "tracker-cannula")]
    TrackerCannula,

    [EnumMember(Value = "tracker-reservoir")]
    TrackerReservoir,

    [EnumMember(Value = "tracker-battery")]
    TrackerBattery,

    [EnumMember(Value = "tracker-consumable")]
    TrackerConsumable,

    [EnumMember(Value = "tracker-appointment")]
    TrackerAppointment,

    [EnumMember(Value = "tracker-reminder")]
    TrackerReminder,

    [EnumMember(Value = "tracker-custom")]
    TrackerCustom,

    // Generic
    [EnumMember(Value = "chart-1")]
    Profile,

    [EnumMember(Value = "chart-2")]
    Override,

    [EnumMember(Value = "muted-foreground")]
    MutedForeground,

    [EnumMember(Value = "primary")]
    Primary,
}
